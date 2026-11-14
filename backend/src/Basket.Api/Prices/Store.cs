using System.Text.RegularExpressions;

namespace Basket.Api.Prices;

/// <summary>The shops basket reads prices from. SuperValu is a later milestone.</summary>
public enum Store
{
    Lidl,
    Aldi,
    Tesco,
    Dunnes,
}

public static partial class Stores
{
    public static IReadOnlyList<Store> All { get; } = [Store.Lidl, Store.Aldi, Store.Tesco, Store.Dunnes];

    /// <summary>The shop named by <paramref name="value"/> (any case), as it appears in an address or a posted batch.</summary>
    public static bool TryParse(string? value, out Store store)
    {
        store = default;
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                store = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The lower-case name a shop goes by in addresses and in the API (<c>tesco</c>).</summary>
    public static string Slug(this Store store) => store.ToString().ToLowerInvariant();

    public static string DisplayName(this Store store) => store switch
    {
        Store.Lidl => "Lidl",
        Store.Aldi => "Aldi",
        Store.Tesco => "Tesco",
        Store.Dunnes => "Dunnes",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    public static string Host(this Store store) => store switch
    {
        Store.Lidl => "www.lidl.ie",
        Store.Aldi => "www.aldi.ie",
        Store.Tesco => "www.tesco.ie",
        Store.Dunnes => "www.dunnesstoresgrocery.com",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    /// <summary>
    /// Who reads this shop's prices: <c>nightly</c> (the VPS job) or <c>pc</c> (an ordinary browser on the owner's home
    /// connection, writing through the token-signed ingest endpoint, <c>POST /api/ingest/prices</c>). tesco.ie and www.aldi.ie answer the VPS with
    /// "Access Denied" (checked 2026-11-05) and dunnesstoresgrocery.com with Cloudflare's challenge (checked 2026-11-08);
    /// basket does not try to get round either. A shop read by <c>pc</c> also posts its whole catalog to
    /// <c>POST /api/ingest/catalog</c>; a <c>nightly</c> shop's catalog is read by the server in process through the same
    /// <c>CatalogRecorder</c>. Aldi is <c>nightly</c> too: its product API on asl.api.aldi.ie is open to the server, only www.aldi.ie is not
    /// (<c>AldiCatalogSource</c>, docs/decisions.md).
    /// </summary>
    public static string ReadBy(this Store store) => store is Store.Lidl or Store.Aldi ? "nightly" : "pc";

    /// <summary>The card whose price counts for this shop (Q7 of the plan).</summary>
    public static string LoyaltyScheme(this Store store) => store switch
    {
        Store.Lidl => "Lidl Plus",
        Store.Tesco => "Clubcard",
        _ => string.Empty,
    };

    /// <summary>What a pasted address has to look like, for the message that explains a refusal.</summary>
    public static string UrlExample(this Store store) => store switch
    {
        Store.Lidl => "https://www.lidl.ie/p/aubergine/p11214436",
        Store.Aldi => "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541",
        Store.Tesco => "https://www.tesco.ie/groceries/en-IE/products/254317390",
        Store.Dunnes => "https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    /// <summary>
    /// Accepts only an https product page on the shop's own host and returns it without query or fragment. The
    /// fetchers only ever call addresses that passed here, so a pasted link cannot aim them elsewhere. Tesco's /shop
    /// and /groceries forms both come back as the /groceries form.
    /// </summary>
    public static bool TryNormalizeProductUrl(Store store, string? raw, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 400 || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var host = store.Host();
        if (parsed.Scheme != Uri.UriSchemeHttps || !(parsed.Host == host || host == "www." + parsed.Host) || !parsed.IsDefaultPort)
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        var matches = store switch
        {
            Store.Lidl => LidlPath().IsMatch(path),
            Store.Aldi => AldiPath().IsMatch(path),
            Store.Tesco => TescoPath().IsMatch(path),
            Store.Dunnes => DunnesPath().IsMatch(path),
            _ => false,
        };
        if (!matches)
        {
            return false;
        }

        if (store == Store.Tesco)
        {
            // tesco.ie's canonical address is /shop/..., the older /groceries/... redirects to it. Both are one key, the
            // /groceries form the stored listings already use, so a /shop address finds an existing listing.
            path = "/groceries/en-IE/products/" + TescoPath().Match(path).Groups["id"].Value;
        }

        url = new Uri($"https://{host}{path}");
        return true;
    }

    /// <summary>Lidl's own product number, the key the offer pages list a product under (the "p11214436" in its address).</summary>
    public static string? LidlProductId(Uri url)
    {
        var match = LidlPath().Match(url.AbsolutePath);
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"^/p/[a-z0-9-]+/p(?<id>\d{4,12})$", RegexOptions.IgnoreCase)]
    private static partial Regex LidlPath();

    [GeneratedRegex(@"^/product/[a-z0-9-]+-\d{6,}$", RegexOptions.IgnoreCase)]
    private static partial Regex AldiPath();

    [GeneratedRegex(@"^/product/[a-z0-9-]+-\d{6,12}$", RegexOptions.IgnoreCase)]
    private static partial Regex DunnesPath();

    [GeneratedRegex(@"^/(?:groceries|shop)/en-IE/products/(?<id>\d{4,12})$", RegexOptions.IgnoreCase)]
    private static partial Regex TescoPath();
}
