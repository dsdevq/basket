using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;

namespace Basket.Api.Prices;

/// <summary>One product a shop's search listed: where its page is and the shop's own photo of it.</summary>
public sealed record ShopSearchResult(string Store, string Url, string Title, string? ImageUrl);

/// <summary>
/// How one shop took part in a search. <see cref="Searched"/> is false for the shops the server cannot search (their sites refuse
/// it); <see cref="Note"/> then says why, so the screen can point at paste-a-link instead of showing nothing.
/// </summary>
public sealed record ShopSearchStatus(string Store, string Name, bool Searched, string? Note);

public sealed record ShopSearchResponse(IReadOnlyList<ShopSearchResult> Results, IReadOnlyList<ShopSearchStatus> Stores);

/// <summary>
/// Searching a shop by name from inside the app. Only Lidl can be searched from the VPS (tesco.ie, aldi.ie and the Dunnes grocery
/// site refuse it, see <see cref="Stores.ReadBy"/>); the other shops are named in the answer as not searched, never tried.
/// </summary>
public static class ShopSearch
{
    public const int MaxQueryLength = 80;

    /// <summary>Most results one search returns: enough for a phone screen of tiles, few enough to scan.</summary>
    public const int MaxResults = 12;

    public static bool TryCleanQuery(string? raw, out string query)
    {
        query = Data.UserNames.Clean(raw, MaxQueryLength) ?? string.Empty;
        return query.Length >= 2;
    }

    public static async Task<ShopSearchResponse> RunAsync(IPageFetcher fetcher, string query, CancellationToken ct)
    {
        var page = await fetcher.GetAsync(new Uri($"https://{Store.Lidl.Host()}/q/search?q={Uri.EscapeDataString(query)}"), ct);
        var lidl = page.Status switch
        {
            FetchStatus.Ok => new ShopSearchStatus("lidl", "Lidl", true, null),
            FetchStatus.Blocked => new ShopSearchStatus("lidl", "Lidl", false, "Lidl's site refused the search. Paste the product's link instead."),
            _ => new ShopSearchStatus("lidl", "Lidl", false, "Lidl's search could not be reached. Try again, or paste the product's link."),
        };

        var results = page.Status == FetchStatus.Ok
            ? LidlParser.ParseSearchTiles(page.Body!).DistinctBy(r => r.Url, StringComparer.Ordinal).Take(MaxResults).ToList()
            : [];

        var others = Stores.All.Where(s => s != Store.Lidl).Select(s =>
            new ShopSearchStatus(s.Slug(), s.DisplayName(), false, $"{s.DisplayName()} cannot be searched from here. Paste a product link on the Prices tab."));
        return new ShopSearchResponse(results, [lidl, .. others]);
    }
}
