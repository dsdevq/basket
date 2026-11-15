using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Catalog;

/// <summary>One closed (or failed) sweep of a shop's catalog, from its <c>price_pull_run</c> row: <c>Seen</c> is what it listed, <c>Live</c> the live catalog after it.</summary>
public sealed record SweepRunResponse(DateTimeOffset FinishedAt, string Outcome, int Seen, int Live, string? Message);

/// <summary>A sweep that listed <see cref="Seen"/> products where the last good one listed <see cref="Previous"/>: under <see cref="CatalogHealth.ShrinkPercent"/> percent of it.</summary>
public sealed record SweepShrinkResponse(int Seen, int Previous);

/// <summary>
/// How one shop's catalog is doing. <see cref="State"/> is <c>never</c> (no sweep run is recorded: the sweep has not run since the
/// column that tells sweeps from shelf reads existed, or the shop's reader has not run), <c>fresh</c>, or <c>stale</c> (no good
/// sweep within <see cref="StaleAfterDays"/> days). <see cref="LastSweep"/> is the latest sweep whatever its outcome;
/// <see cref="LastOkAt"/> the latest that finished <c>ok</c>. <see cref="DiscontinuedOnShelf"/> counts the shelf's pages that the
/// shop no longer lists. <see cref="Shrunk"/> is set when the latest sweep listed under 90 percent of the last good one.
/// </summary>
public sealed record CatalogHealthResponse(
    string State,
    int StaleAfterDays,
    SweepRunResponse? LastSweep,
    DateTimeOffset? LastOkAt,
    int Live,
    int Discontinued,
    int DiscontinuedOnShelf,
    SweepShrinkResponse? Shrunk);

/// <summary>
/// The health of each shop's catalog, worked out from the <c>price_pull_run</c> rows of kind <see cref="RunKind.Sweep"/> (the rows
/// written before the kind column existed have none and are skipped) and the catalog tables. Nothing is guessed: a shop with no
/// sweep row says <c>never</c>. The sweeps themselves run elsewhere (the server's own for Lidl and Aldi, the PC reader's through
/// <c>POST /api/ingest/catalog/complete</c> for Tesco and Dunnes); this only reads what they left.
/// </summary>
public static class CatalogHealth
{
    /// <summary>A sweep that listed less than this share of the last good one's count is flagged.</summary>
    public const int ShrinkPercent = CatalogIngest.DiscontinueGuardPercent;

    /// <summary>A shop the server sweeps every night is stale after two days; one the PC reads about weekly, after nine.</summary>
    public const int NightlyStaleDays = 2;

    public const int PcStaleDays = 9;

    /// <summary>How far back the sweep rows are read: far past the longest stale limit, so the table's growth never grows the query.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);

    public static int StaleAfterDays(Store store) => store.ReadBy() == "pc" ? PcStaleDays : NightlyStaleDays;

    public static async Task<Dictionary<Store, CatalogHealthResponse>> ForAllAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - Window;
        var runs = (await db.PricePullRuns.AsNoTracking()
                .Where(r => r.Kind == RunKind.Sweep && r.FinishedAt >= since)
                .Select(r => new SweepRun(r.Store, r.FinishedAt, r.Outcome, r.Read, r.Listings, r.Message))
                .ToListAsync(ct))
            .ToLookup(r => r.Store);

        var counts = (await db.StoreProducts.AsNoTracking()
                .GroupBy(p => p.Store)
                .Select(g => new { Store = g.Key, Live = g.Count(p => p.DiscontinuedAt == null), Discontinued = g.Count(p => p.DiscontinuedAt != null) })
                .ToListAsync(ct))
            .ToDictionary(c => c.Store);

        var onShelf = (await db.StoreListings.AsNoTracking()
                .Where(l => db.StoreProducts.Any(p => p.Id == l.StoreProductId && p.DiscontinuedAt != null))
                .GroupBy(l => l.Store)
                .Select(g => new { Store = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(c => c.Store, c => c.Count);

        return Stores.All.ToDictionary(
            store => store,
            store => Build(
                store,
                runs[store].ToList(),
                counts.TryGetValue(store, out var count) ? count.Live : 0,
                counts.TryGetValue(store, out count) ? count.Discontinued : 0,
                onShelf.GetValueOrDefault(store),
                now));
    }

    /// <summary>The raw numbers of one sweep row, as <see cref="Build"/> takes them.</summary>
    public sealed record SweepRun(Store Store, DateTimeOffset FinishedAt, PullOutcome Outcome, int Read, int Listings, string? Message);

    public static CatalogHealthResponse Build(
        Store store, IReadOnlyList<SweepRun> sweeps, int live, int discontinued, int discontinuedOnShelf, DateTimeOffset now)
    {
        var limit = StaleAfterDays(store);
        var ordered = sweeps.OrderByDescending(r => r.FinishedAt).ToList();
        var last = ordered.FirstOrDefault();
        if (last is null)
        {
            return new CatalogHealthResponse("never", limit, null, null, live, discontinued, discontinuedOnShelf, null);
        }

        var good = ordered.FirstOrDefault(r => r.Outcome == PullOutcome.Ok);
        var state = good is not null && now - good.FinishedAt <= TimeSpan.FromDays(limit) ? "fresh" : "stale";

        // The baseline is the last good sweep before the latest one; the first sweep has nothing to shrink from.
        var before = ordered.Skip(1).FirstOrDefault(r => r.Outcome == PullOutcome.Ok);
        var shrunk = before is { Read: > 0 } && last.Read * 100L < before.Read * (long)ShrinkPercent
            ? new SweepShrinkResponse(last.Read, before.Read)
            : null;

        return new CatalogHealthResponse(
            state,
            limit,
            new SweepRunResponse(last.FinishedAt, last.Outcome.ToString().ToLowerInvariant(), last.Read, last.Listings, last.Message),
            good?.FinishedAt,
            live,
            discontinued,
            discontinuedOnShelf,
            shrunk);
    }
}
