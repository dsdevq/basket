using Basket.Api.Data;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Sources;

/// <summary>
/// Centra, read once a night from its Special Offers page (<c>centra.ie/offers</c>, which robots.txt allows). Centra publishes no
/// catalogue and no shelf prices (stores are franchised), so the whole of what basket knows about Centra is that page: about forty
/// weekly offers, each with its price or multibuy and the day it is valid until. Every offer is recorded in the combined catalog as a
/// price with that end date and the promo text "Weekly offer", so matching proposes products for it by the same rule as for the other
/// shops, and the Plan and the comparison stop counting it the day it ends (docs/decisions.md, "Centra"). The page turns over by
/// design, so a finished read delists what is no longer on it without the 90 percent guard the other sweeps use. A refusal ends the
/// read; nothing is retried or worked around.
/// </summary>
public sealed class CentraOffersSource(
    IPageFetcher fetcher,
    CatalogRecorder recorder,
    CatalogIngest sweeps,
    AppDbContext db,
    TimeProvider clock,
    ILogger<CentraOffersSource> log) : IStorePriceSource
{
    /// <summary>The offers page. centra.ie, not www.centra.ie: the www address redirects to another host name, which the polite fetcher refuses.</summary>
    public const string OffersUrl = "https://centra.ie/offers";

    public Store Store => Store.Centra;

    public bool SweepsCatalog => true;

    public async Task<StorePull> PullAsync(IReadOnlyList<ListingToCheck> listings, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var page = await fetcher.GetAsync(new Uri(OffersUrl), ct);
        if (page.Status == FetchStatus.Blocked)
        {
            await RecordSweepAsync(startedAt, PullOutcome.Blocked, 0, StorePulls.BlockedMessage(page.Detail!), ct);
            return StorePulls.Blocked(listings, page.Detail!);
        }

        if (page.Status != FetchStatus.Ok)
        {
            return await FailAsync(listings, startedAt, $"Centra's offers page was not read ({page.Detail ?? "no detail"}).", ct);
        }

        var offers = CentraParser.Parse(page.Body!, PriceRecorder.DublinDay(startedAt));
        var readings = new List<CatalogReading>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var unusable = offers.Unusable;
        foreach (var item in offers.Items)
        {
            if (!CatalogReading.TryCreate(Store.Centra, item, out var reading, out var reason))
            {
                unusable++;
                rejected.Add(item.ExternalId ?? string.Empty);
                log.LogDebug("A Centra offer was not usable: {Reason}", reason);
            }
            else if (seen.Add(reading!.ExternalId))
            {
                readings.Add(reading);
            }
        }

        if (readings.Count == 0)
        {
            return await FailAsync(listings, startedAt, "Centra's offers page had no offer basket could read, the page may have changed.", ct);
        }

        await recorder.RecordAsync(Store.Centra, readings, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var close = await sweeps.CloseAsync(Store.Centra, seen.Count, startedAt, ct, guarded: false);
        var message = close.Message;
        if (unusable > 0)
        {
            message = PriceRecorder.Truncate($"{unusable} offers on the page had no name, price or end date and were skipped. {message}".Trim(), 500);
        }

        await RecordSweepAsync(startedAt, close.Outcome, close.Touched, message, ct, close.Live);

        var byId = readings.ToDictionary(r => r.ExternalId, StringComparer.Ordinal);
        var results = new Dictionary<Guid, ListingPull>();
        foreach (var listing in listings)
        {
            var id = Stores.ProductId(Store.Centra, listing.Url.ToString());
            results[listing.ListingId] = id is not null && rejected.Contains(id)
                ? ListingPull.Failed("Centra lists this product but basket could not read its offer.")
                : id is not null && byId.TryGetValue(id, out var reading) && reading.Price.Price is not null
                ? ListingPull.Priced(new PriceQuote(
                    reading.Name,
                    reading.Price.Price,
                    null,
                    null,
                    null,
                    reading.Price.PromoText,
                    reading.Price.ValidFrom,
                    reading.Price.ValidUntil,
                    true,
                    reading.ImageUrl,
                    reading.Category))
                : ListingPull.Failed("Centra's offers no longer list this product. Offers last a week or two.");
        }

        log.LogInformation("Centra: {Offers} offers listed, {Skipped} skipped", readings.Count, unusable);
        return new StorePull(close.Outcome, message, results);
    }

    private async Task<StorePull> FailAsync(IReadOnlyList<ListingToCheck> listings, DateTimeOffset startedAt, string message, CancellationToken ct)
    {
        await RecordSweepAsync(startedAt, PullOutcome.Failed, 0, message, ct);
        return new StorePull(PullOutcome.Failed, message, listings.ToDictionary(l => l.ListingId, _ => ListingPull.Failed(message)));
    }

    /// <summary>The read's own run row beside the shelf row the nightly job writes: <c>Read</c> is the offers listed, <c>Listings</c> the live offers after it.</summary>
    private async Task RecordSweepAsync(DateTimeOffset startedAt, PullOutcome outcome, int read, string? message, CancellationToken ct, int? live = null)
    {
        db.PricePullRuns.Add(new PricePullRun
        {
            Id = Guid.NewGuid(),
            Store = Store.Centra,
            StartedAt = startedAt,
            FinishedAt = clock.GetUtcNow(),
            Outcome = outcome,
            Kind = RunKind.Sweep,
            Listings = live ?? await db.StoreProducts.CountAsync(p => p.Store == Store.Centra && p.DiscontinuedAt == null, ct),
            Read = read,
            Message = PriceRecorder.Truncate(message, 500),
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
