using Basket.Api.Data;
using Basket.Api.Prices.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Catalog;

public sealed record CatalogIngestRequest(string? Store, IReadOnlyList<CatalogItem?>? Items);

public sealed record CatalogCompleteRequest(string? Store, int? Seen, DateTimeOffset? StartedAt);

/// <summary>
/// How a closed sweep ended. <see cref="Guarded"/> is true when too little of the known catalog was seen and nothing was
/// marked discontinued; <see cref="Outcome"/> is what the <c>price_pull_run</c> row says (<c>Ok</c>, <c>Partial</c>, <c>Failed</c>).
/// </summary>
public sealed record CatalogCompleteResponse(string Store, int Seen, int PreviousLive, int Discontinued, bool Guarded, string Outcome);

/// <summary>
/// The HTTP door of the combined catalog, beside <see cref="PriceIngest"/> and written the same way: each product stands
/// alone, a bad one is rejected with a stable reason and the others are kept. The shop is one value for the whole batch.
/// Products are dated by the server's clock, never the poster's, and written through <see cref="CatalogRecorder"/>, the same
/// path the server's own sweeps use. A sweep is closed by <see cref="CompleteAsync"/>.
/// </summary>
public sealed class CatalogIngest(AppDbContext db, CatalogRecorder recorder, TimeProvider clock)
{
    public const int MaxBatch = PriceIngest.MaxBatch;

    /// <summary>A sweep that saw less than this share of the shop's known live products must not delist the rest: it half-failed.</summary>
    public const int DiscontinueGuardPercent = 90;

    /// <summary>How far back a sweep may have started (the slowest PC sweep takes hours) and how far ahead a poster's clock may run.</summary>
    public static readonly TimeSpan MaxSweepAge = TimeSpan.FromHours(24);

    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static class Reasons
    {
        public const string UnknownStore = PriceIngest.Reasons.UnknownStore;
        public const string StoreNotIngested = PriceIngest.Reasons.StoreNotIngested;
        public const string Duplicate = PriceIngest.Reasons.Duplicate;
    }

    /// <summary>The shops that may post a catalog: the ones read from the PC. The server reads the others itself.</summary>
    public static bool Accepts(Store store) => store.ReadBy() == "pc";

    public async Task<IngestResponse> IngestAsync(string? storeName, IReadOnlyList<CatalogItem?> items, CancellationToken ct)
    {
        var results = new IngestItemResult[items.Count];
        string? shopReason = null;
        Store store = default;
        if (!Stores.TryParse(storeName, out store))
        {
            shopReason = Reasons.UnknownStore;
        }
        else if (!Accepts(store))
        {
            shopReason = Reasons.StoreNotIngested;
        }

        if (shopReason is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                results[i] = new IngestItemResult(i, false, shopReason);
            }

            return Respond(results);
        }

        var readings = new List<CatalogReading>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (!CatalogReading.TryCreate(store, items[i]!, out var reading, out var reason))
            {
                results[i] = new IngestItemResult(i, false, reason);
            }
            else if (!seen.Add(reading!.ExternalId))
            {
                // The first of a repeated id stands; a batch that lists a product twice is a reader bug worth seeing.
                results[i] = new IngestItemResult(i, false, Reasons.Duplicate);
            }
            else
            {
                results[i] = new IngestItemResult(i, true, null);
                readings.Add(reading);
            }
        }

        await recorder.RecordAsync(store, readings, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return Respond(results);
    }

    private static IngestResponse Respond(IngestItemResult[] results)
    {
        var accepted = results.Count(r => r.Accepted);
        return new IngestResponse(accepted, results.Length - accepted, results);
    }

    /// <summary>
    /// Closes a sweep of <paramref name="store"/> that began at <paramref name="startedAt"/>. Products the sweep did not list
    /// (last seen before it began) are marked discontinued, but only when the sweep covered at least
    /// <see cref="DiscontinueGuardPercent"/> percent of the live products known before it: both the reader's own
    /// <paramref name="seen"/> count and the server's count of the known products it actually saw must clear the bar, so a
    /// half-failed sweep, a lying count or a skewed clock cannot delist half a shop. One <c>price_pull_run</c> row records the sweep.
    /// </summary>
    public async Task<CatalogCompleteResponse> CompleteAsync(Store store, int seen, DateTimeOffset startedAt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var shop = db.StoreProducts.Where(p => p.Store == store);
        var previousLive = await shop.CountAsync(p => p.DiscontinuedAt == null && p.FirstSeenAt < startedAt, ct);
        var seenKnown = await shop.CountAsync(p => p.DiscontinuedAt == null && p.FirstSeenAt < startedAt && p.LastSeenAt >= startedAt, ct);
        var touched = await shop.CountAsync(p => p.LastSeenAt >= startedAt, ct);

        var passes = previousLive == 0
            || (seen * 100L >= previousLive * (long)DiscontinueGuardPercent && seenKnown * 100L >= previousLive * (long)DiscontinueGuardPercent);
        var discontinued = passes
            ? await shop.Where(p => p.DiscontinuedAt == null && p.LastSeenAt < startedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DiscontinuedAt, now), ct)
            : 0;

        var live = await shop.CountAsync(p => p.DiscontinuedAt == null, ct);
        var outcome = passes ? PullOutcome.Ok : seenKnown == 0 ? PullOutcome.Failed : PullOutcome.Partial;
        db.PricePullRuns.Add(new PricePullRun
        {
            Id = Guid.NewGuid(),
            Store = store,
            StartedAt = startedAt,
            FinishedAt = now,
            Outcome = outcome,
            Listings = live,
            Read = touched,
            Message = passes
                ? (discontinued > 0 ? $"{discontinued} products were no longer listed and are marked discontinued." : null)
                : PriceRecorder.Truncate(
                    $"The catalog sweep listed {seenKnown} of the {previousLive} known products (the reader counted {seen}); nothing was marked discontinued.",
                    500),
        });
        await db.SaveChangesAsync(ct);

        return new CatalogCompleteResponse(store.Slug(), seen, previousLive, discontinued, !passes, outcome.ToString());
    }
}
