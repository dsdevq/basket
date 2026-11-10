using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Ingest;

/// <summary>One reading as the PC-side reader posts it: which shop page it came from, and what that page said.</summary>
public sealed record IngestPrice(
    string? Store,
    string? Url,
    string? Title,
    decimal? Price,
    decimal? WasPrice,
    decimal? LoyaltyPrice,
    string? LoyaltyScheme,
    string? PromoText,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    bool? InStock,
    string? ImageUrl = null);

public sealed record IngestRequest(IReadOnlyList<IngestPrice>? Prices);

/// <summary>What became of one posted reading. <see cref="Reason"/> is a stable code, null when accepted.</summary>
public sealed record IngestItemResult(int Index, bool Accepted, string? Reason);

public sealed record IngestResponse(int Accepted, int Rejected, IReadOnlyList<IngestItemResult> Results);

/// <summary>
/// Turns a posted batch into dated <see cref="PriceObservation"/> rows through <see cref="PriceRecorder"/>, the same
/// write path as the nightly job. Each reading stands alone: a bad one is rejected with a reason and the others are
/// kept. A reading may carry the product photo's address (<c>imageUrl</c>), stored on the listing when it is a plain https address (otherwise
/// ignored, the price is still kept); the picture itself is never fetched or kept. The reading is dated by the server's clock, never by the poster, so a stale or replayed batch cannot
/// overwrite a newer reading of the same day with an older one.
/// </summary>
public sealed class PriceIngest(AppDbContext db, PriceRecorder recorder, TimeProvider clock)
{
    public const int MaxBatch = 200;
    public const decimal MaxPrice = 9999.99m;

    public static class Reasons
    {
        public const string UnknownStore = "unknown_store";
        public const string StoreNotIngested = "store_not_ingested";
        public const string UrlInvalid = "url_invalid";
        public const string PriceInvalid = "price_invalid";
        public const string NoPrice = "no_price";
        public const string DatesInvalid = "dates_invalid";
        public const string UnknownListing = "unknown_listing";
        public const string Duplicate = "duplicate";
    }

    private sealed record Candidate(int Index, Store Store, string Url, PriceQuote Quote);

    /// <summary>How one shop's part of a batch went, for the run the Prices page reports.</summary>
    private sealed class StoreTally
    {
        public int Total { get; set; }

        public int Accepted { get; set; }

        public SortedSet<string> Reasons { get; } = new(StringComparer.Ordinal);
    }

    public async Task<IngestResponse> IngestAsync(IReadOnlyList<IngestPrice> items, CancellationToken ct)
    {
        var results = new IngestItemResult?[items.Count];
        var candidates = new List<Candidate>();
        for (var i = 0; i < items.Count; i++)
        {
            var reason = Validate(i, items[i], out var candidate);
            if (reason is not null)
            {
                results[i] = new IngestItemResult(i, false, reason);
            }
            else
            {
                candidates.Add(candidate!);
            }
        }

        var urls = candidates.Select(c => c.Url).Distinct().ToList();
        var listings = (await db.StoreListings.Where(l => urls.Contains(l.Url)).ToListAsync(ct))
            .GroupBy(l => (l.Store, l.Url))
            .ToDictionary(g => g.Key, g => g.ToList());

        var now = clock.GetUtcNow();
        var recorded = new HashSet<(Store, string)>();
        var perStore = new Dictionary<Store, StoreTally>();
        foreach (var candidate in candidates)
        {
            if (!perStore.TryGetValue(candidate.Store, out var tally))
            {
                perStore[candidate.Store] = tally = new StoreTally();
            }

            string? reason = null;
            if (!listings.TryGetValue((candidate.Store, candidate.Url), out var matching))
            {
                reason = Reasons.UnknownListing;
            }
            else if (!recorded.Add((candidate.Store, candidate.Url)))
            {
                reason = Reasons.Duplicate;
            }
            else
            {
                foreach (var listing in matching)
                {
                    await recorder.RecordAsync(listing, candidate.Quote, now, ct);
                }
            }

            results[candidate.Index] = new IngestItemResult(candidate.Index, reason is null, reason);
            tally.Total++;
            if (reason is null)
            {
                tally.Accepted++;
            }
            else
            {
                tally.Reasons.Add(reason);
            }
        }

        foreach (var (store, tally) in perStore)
        {
            var (total, accepted) = (tally.Total, tally.Accepted);
            db.PricePullRuns.Add(new PricePullRun
            {
                Id = Guid.NewGuid(),
                Store = store,
                StartedAt = now,
                FinishedAt = now,
                Outcome = accepted == total ? PullOutcome.Ok : accepted > 0 ? PullOutcome.Partial : PullOutcome.Failed,
                Listings = total,
                Read = accepted,
                Message = accepted == total
                    ? null
                    : PriceRecorder.Truncate($"The PC reader posted {total} readings, {total - accepted} were refused ({string.Join(", ", tally.Reasons)}).", 500),
            });
        }

        await db.SaveChangesAsync(ct);

        var all = results.Select(r => r!).ToList();
        var acceptedCount = all.Count(r => r.Accepted);
        return new IngestResponse(acceptedCount, all.Count - acceptedCount, all);
    }

    /// <summary>The reason a reading is unusable, or null with <paramref name="candidate"/> set when it is fine.</summary>
    private static string? Validate(int index, IngestPrice item, out Candidate? candidate)
    {
        candidate = null;
        if (!Stores.TryParse(item.Store, out var store))
        {
            return Reasons.UnknownStore;
        }

        // Lidl is read by the server itself; the PC reader's scope is the shops the server cannot reach.
        if (store.ReadBy() != "pc")
        {
            return Reasons.StoreNotIngested;
        }

        if (!Stores.TryNormalizeProductUrl(store, item.Url, out var url))
        {
            return Reasons.UrlInvalid;
        }

        if (!IsMoney(item.Price) || !IsMoney(item.WasPrice) || !IsMoney(item.LoyaltyPrice))
        {
            return Reasons.PriceInvalid;
        }

        var inStock = item.InStock ?? true;
        if (item.Price is null && item.LoyaltyPrice is null && inStock)
        {
            return Reasons.NoPrice;
        }

        if (item.ValidFrom is { } from && item.ValidUntil is { } until && from > until)
        {
            return Reasons.DatesInvalid;
        }

        // The photo is optional and never costs a price: an address that is absent or not a plain https one is ignored.
        var image = ImageUrls.TryNormalize(item.ImageUrl, out var normalized) ? normalized : null;

        var scheme = item.LoyaltyPrice is null
            ? null
            : UserNames.Clean(item.LoyaltyScheme, 32) ?? (store.LoyaltyScheme() is { Length: > 0 } own ? own : null);
        candidate = new Candidate(
            index,
            store,
            url.ToString(),
            new PriceQuote(
                UserNames.Clean(item.Title, 300),
                item.Price,
                item.WasPrice,
                item.LoyaltyPrice,
                scheme,
                UserNames.Clean(item.PromoText, 200),
                item.ValidFrom,
                item.ValidUntil,
                inStock,
                image));
        return null;
    }

    /// <summary>Absent, or a positive euro amount with at most cents.</summary>
    private static bool IsMoney(decimal? value) =>
        value is null || (value > 0 && value <= MaxPrice && decimal.Round(value.Value, 2) == value);
}
