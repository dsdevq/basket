using System.Text.RegularExpressions;

namespace Basket.Api.Prices;

/// <summary>The shops basket reads prices from. Dunnes and SuperValu are later milestones.</summary>
public enum Store
{
    Lidl,
    Aldi,
    Tesco,
}

public static partial class Stores
{
    public static IReadOnlyList<Store> All { get; } = [Store.Lidl, Store.Aldi, Store.Tesco];

    public static string DisplayName(this Store store) => store switch
    {
        Store.Lidl => "Lidl",
        Store.Aldi => "Aldi",
        Store.Tesco => "Tesco",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    public static string Host(this Store store) => store switch
    {
        Store.Lidl => "www.lidl.ie",
        Store.Aldi => "www.aldi.ie",
        Store.Tesco => "www.tesco.ie",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    /// <summary>
    /// Who reads this shop's prices: <c>nightly</c> (the VPS job) or <c>pc</c> (an ordinary browser on the owner's home
    /// connection, writing through the ingest endpoint of a later task). tesco.ie and aldi.ie answer the VPS with
    /// "Access Denied" (checked 2026-11-05) and basket does not try to get round that.
    /// </summary>
    public static string ReadBy(this Store store) => store == Store.Lidl ? "nightly" : "pc";

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
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    /// <summary>
    /// Accepts only an https product page on the shop's own host and returns it without query or fragment. The
    /// fetchers only ever call addresses that passed here, so a pasted link cannot aim them elsewhere.
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
            _ => false,
        };
        if (!matches)
        {
            return false;
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

    [GeneratedRegex(@"^/groceries/en-IE/products/\d{4,12}$", RegexOptions.IgnoreCase)]
    private static partial Regex TescoPath();
}
