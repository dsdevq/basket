using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;
using Microsoft.Extensions.Options;

namespace Basket.Api.Prices.Sources;

/// <summary>
/// Lidl: the weekly-offer pages first (one request each, however many products are linked), then the product page of
/// anything they did not list. Lidl Plus prices come with the offer tiles and need no login.
/// </summary>
public sealed class LidlPriceSource(IPageFetcher fetcher, IOptions<PricesOptions> options, ILogger<LidlPriceSource> log) : IStorePriceSource
{
    public Store Store => Store.Lidl;

    public async Task<StorePull> PullAsync(IReadOnlyList<ListingToCheck> listings, CancellationToken ct)
    {
        var offers = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        foreach (var page in options.Value.LidlOfferPages)
        {
            var result = await fetcher.GetAsync(new Uri(page), ct);
            switch (result.Status)
            {
                case FetchStatus.Blocked:
                    return StorePulls.Blocked(listings, result.Detail!);
                case FetchStatus.Ok:
                    foreach (var (id, quote) in LidlParser.ParseOfferTiles(result.Body!))
                    {
                        offers[id] = quote;
                    }

                    break;
                default:
                    // An offer page being down only costs the offers on it; the product pages are still tried.
                    log.LogWarning("Lidl offer page {Page} was not read: {Detail}", page, result.Detail);
                    break;
            }
        }

        var results = new Dictionary<Guid, ListingPull>();
        string? hint = null;
        foreach (var listing in listings)
        {
            if (Stores.LidlProductId(listing.Url) is { } id && offers.TryGetValue(id, out var offer))
            {
                results[listing.ListingId] = ListingPull.Priced(offer);
                continue;
            }

            var page = await fetcher.GetAsync(listing.Url, ct);
            switch (page.Status)
            {
                case FetchStatus.Blocked:
                    return StorePulls.Blocked(listings, page.Detail!);
                case FetchStatus.Ok:
                    results[listing.ListingId] = ReadProductPage(page.Body!);
                    break;
                case FetchStatus.NotFound:
                    results[listing.ListingId] = ListingPull.Failed("Lidl no longer has this page (404). Link another product page.");
                    break;
                default:
                    hint ??= page.Detail;
                    results[listing.ListingId] = ListingPull.Failed(page.Detail ?? "The Lidl page could not be read.");
                    break;
            }
        }

        return StorePulls.Summarize(listings, results, hint);
    }

    private static ListingPull ReadProductPage(string html)
    {
        try
        {
            var quote = LidlParser.ParseProductPage(html);
            return quote.Price is null
                ? new ListingPull(quote, null) // on Lidl's site but not priced this week: a reading, "no price"
                : ListingPull.Priced(quote);
        }
        catch (ParseException ex)
        {
            return ListingPull.Failed(ex.Message);
        }
    }
}
