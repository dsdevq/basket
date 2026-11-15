using Basket.Api.Data;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;
using Microsoft.Extensions.Options;

namespace Basket.Api.Prices.Sources;

/// <summary>
/// Lidl: the weekly-offer pages, one request each however many products are linked. Lidl Plus prices come with the offer
/// tiles and need no login. A linked product the offer pages do not list is left alone: the catalog sweep prices it, and
/// no product page is opened for it.
/// </summary>
public sealed class LidlPriceSource(IPageFetcher fetcher, IOptions<PricesOptions> options, ILogger<LidlPriceSource> log) : IStorePriceSource
{
    public Store Store => Store.Lidl;

    public async Task<StorePull> PullAsync(IReadOnlyList<ListingToCheck> listings, CancellationToken ct)
    {
        var offers = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        string? offerPageError = null;
        foreach (var page in options.Value.LidlOfferPages)
        {
            var result = await fetcher.GetAsync(new Uri(page), ct);
            switch (result.Status)
            {
                case FetchStatus.Blocked:
                    return StorePulls.Blocked(listings, result.Detail!);
                case FetchStatus.Ok:
                    var tiles = LidlParser.ParseOfferTiles(result.Body!);
                    foreach (var (id, quote) in tiles)
                    {
                        offers[id] = quote;
                    }

                    if (tiles.Count == 0)
                    {
                        offerPageError ??= "A Lidl offers page had no priced products, the page layout may have changed.";
                    }

                    break;
                default:
                    log.LogWarning("Lidl offer page {Page} was not read: {Detail}", page, result.Detail);
                    offerPageError ??= $"A Lidl offers page was not read ({result.Detail ?? "no detail"}).";
                    break;
            }
        }

        var results = new Dictionary<Guid, ListingPull>();
        foreach (var listing in listings)
        {
            results[listing.ListingId] = Stores.LidlProductId(listing.Url) is { } id && offers.TryGetValue(id, out var offer)
                ? ListingPull.Priced(offer)
                : ListingPull.Unlisted;
        }

        if (offerPageError is null)
        {
            return new StorePull(PullOutcome.Ok, null, results);
        }

        return new StorePull(offers.Count == 0 ? PullOutcome.Failed : PullOutcome.Partial, offerPageError, results);
    }
}
