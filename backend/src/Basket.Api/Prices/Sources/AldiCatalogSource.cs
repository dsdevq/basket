using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Basket.Api.Data;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Fetching;

namespace Basket.Api.Prices.Sources;

/// <summary>
/// Aldi, read as a whole range once a night from its product API (<c>asl.api.aldi.ie</c>, open to the server while
/// <c>www.aldi.ie</c> is not). One request for the category tree, then the range sixty products at a time until
/// <c>totalCount</c>, through the polite fetcher: honest user agent, a gap between requests, and a 403 or 429 ends the sweep
/// with no retry. Every page is recorded in the combined catalog as it arrives (so a refusal half way keeps what was read), a
/// finished sweep delists what Aldi no longer lists (behind the same 90 percent guard as the PC reader's), and a linked product's
/// price comes out of the same read, so Aldi pages are never opened one by one.
/// </summary>
public sealed partial class AldiCatalogSource(
    IPageFetcher fetcher,
    CatalogRecorder recorder,
    CatalogIngest sweeps,
    AppDbContext db,
    TimeProvider clock,
    ILogger<AldiCatalogSource> log) : IStorePriceSource
{
    public const int PageSize = 60;

    /// <summary>Aldi's product API. asl.api.aldi.ie answers the server while www.aldi.ie does not (docs/decisions.md).</summary>
    private const string ApiBase = "https://asl.api.aldi.ie/v3";

    /// <summary>
    /// The most requests one sweep makes, the category tree included: the 60 polite requests a day the owner accepted for Aldi's
    /// terms.
    /// </summary>
    private const int MaxRequests = 60;

    public Store Store => Store.Aldi;

    public bool SweepsCatalog => true;

    public async Task<StorePull> PullAsync(IReadOnlyList<ListingToCheck> listings, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var wanted = listings.Select(l => SkuOf(l.Url)).Where(s => s is not null).Select(s => s!).ToHashSet(StringComparer.Ordinal);
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        var discontinued = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var requests = 0;
        var anyPriced = false;

        var tree = await fetcher.GetAsync(new Uri($"{ApiBase}/product-category-tree?currency=EUR"), ct);
        requests++;
        if (tree.Status == FetchStatus.Blocked)
        {
            return StorePulls.Blocked(listings, tree.Detail!);
        }

        var paths = tree.Status == FetchStatus.Ok ? ReadCategoryPaths(tree.Body!) : new Dictionary<string, string>();
        string? note = tree.Status == FetchStatus.Ok
            ? null
            : $"Aldi's category tree was not read ({tree.Detail ?? "no detail"}), so products are filed under the category names they carry.";

        var complete = false;
        string? stopped = null;
        for (var offset = 0; ; offset += PageSize)
        {
            if (requests >= MaxRequests)
            {
                stopped = $"The sweep stopped at the request limit ({MaxRequests}) before the end of Aldi's range.";
                break;
            }

            var page = await fetcher.GetAsync(new Uri($"{ApiBase}/product-search?currency=EUR&limit={PageSize}&offset={offset}"), ct);
            requests++;
            if (page.Status == FetchStatus.Blocked)
            {
                // What the earlier pages recorded stays; nothing is delisted and nothing is retried.
                return StorePulls.Blocked(listings, page.Detail!);
            }

            if (page.Status != FetchStatus.Ok)
            {
                stopped = $"Aldi's product list stopped at offset {offset} ({page.Detail ?? "no detail"}).";
                break;
            }

            AldiPage parsed;
            try
            {
                parsed = ReadPage(page.Body!, paths);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                log.LogWarning(ex, "Aldi's product list at offset {Offset} was not in the expected shape", offset);
                stopped = $"Aldi's product list at offset {offset} was not in the shape basket reads, the API may have changed.";
                break;
            }

            var live = parsed.Products.Where(p => !p.Discontinued).ToList();
            if (!anyPriced && live.Count > 0 && live.All(p => p.Item.Price is null))
            {
                log.LogWarning("None of the {Count} Aldi products at offset {Offset} carried a price", live.Count, offset);
                stopped = $"Aldi's product list at offset {offset} was not in the shape basket reads (no product had a price), the API may have changed.";
                break;
            }

            anyPriced |= live.Any(p => p.Item.Price is not null);
            var readings = new List<CatalogReading>();
            foreach (var product in parsed.Products)
            {
                if (product.Discontinued)
                {
                    discontinued.Add(product.Sku);
                    continue;
                }

                if (!CatalogReading.TryCreate(Store.Aldi, product.Item, out var reading, out var reason))
                {
                    log.LogDebug("An Aldi product was not usable: {Reason}", reason);
                    continue;
                }

                // Offsets shift while a range changes under a sweep: a repeated product is the same product.
                if (!seen.Add(reading!.ExternalId))
                {
                    continue;
                }

                readings.Add(reading);
                if (wanted.Contains(reading.ExternalId))
                {
                    quotes[reading.ExternalId] = QuoteOf(reading);
                }
            }

            await recorder.RecordAsync(Store.Aldi, readings, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            if (offset + PageSize >= parsed.TotalCount)
            {
                complete = true;
                break;
            }

            if (parsed.Products.Count == 0)
            {
                stopped = $"Aldi's product list ended at offset {offset}, before the {parsed.TotalCount} products it counts.";
                break;
            }
        }

        if (seen.Count == 0)
        {
            var reason = stopped ?? "Aldi's product list was empty, the API may have changed.";
            return Failed(listings, reason);
        }

        var message = stopped ?? note;
        var outcome = complete ? PullOutcome.Ok : PullOutcome.Partial;
        if (complete)
        {
            var close = await sweeps.CloseAsync(Store.Aldi, seen.Count, startedAt, ct);
            outcome = close.Outcome;
            message = close.Message ?? note;
        }

        var results = new Dictionary<Guid, ListingPull>();
        foreach (var listing in listings)
        {
            var sku = SkuOf(listing.Url);
            if (sku is not null && quotes.TryGetValue(sku, out var quote))
            {
                results[listing.ListingId] = ListingPull.Priced(quote);
            }
            else if (sku is not null && discontinued.Contains(sku))
            {
                results[listing.ListingId] = ListingPull.Failed("Aldi has discontinued this product. Link another one.");
            }
            else if (complete)
            {
                results[listing.ListingId] = ListingPull.Failed("Aldi's range no longer lists this product. Link another one.");
            }
            else
            {
                results[listing.ListingId] = ListingPull.Failed(message ?? "The Aldi sweep did not reach this product.");
            }
        }

        var read = results.Values.Count(r => r.Quote is not null);
        if (listings.Count > 0 && read < listings.Count && outcome == PullOutcome.Ok)
        {
            outcome = read == 0 ? PullOutcome.Failed : PullOutcome.Partial;
            message ??= results.Values.Select(r => r.Error).FirstOrDefault(e => e is not null);
        }

        log.LogInformation("Aldi: {Seen} products listed in {Requests} requests, {Read} of {Listings} linked products priced", seen.Count, requests, read, listings.Count);
        return new StorePull(outcome, message, results);
    }

    private static StorePull Failed(IReadOnlyList<ListingToCheck> listings, string message) =>
        new(PullOutcome.Failed, message, listings.ToDictionary(l => l.ListingId, _ => ListingPull.Failed(message)));

    private static PriceQuote QuoteOf(CatalogReading reading) =>
        new(reading.Name, reading.Price.Price, reading.Price.WasPrice, null, null, null, null, null, reading.Price.InStock, reading.ImageUrl, reading.Category);

    /// <summary>Aldi's own id for a product page: the number at the end of its address.</summary>
    internal static string? SkuOf(Uri url)
    {
        var match = SkuAtEnd().Match(url.AbsolutePath);
        return match.Success ? match.Groups["sku"].Value : null;
    }

    private sealed record AldiProduct(string Sku, bool Discontinued, CatalogItem Item);

    private sealed record AldiPage(IReadOnlyList<AldiProduct> Products, int TotalCount);

    /// <summary>
    /// Category id to its path from the tree ("Fresh Food &gt; Dairy &gt; Milk"), outermost first. The catalogue root ("Products") is
    /// not part of a path, as on every other category in basket.
    /// </summary>
    internal static Dictionary<string, string> ReadCategoryPaths(string json)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var nodes = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
            Walk(nodes, [], paths, 0);
        }
        catch (JsonException)
        {
            paths.Clear();
        }

        return paths;
    }

    private static void Walk(JsonElement nodes, List<string> trail, Dictionary<string, string> paths, int depth)
    {
        if (nodes.ValueKind != JsonValueKind.Array || depth > 8)
        {
            return;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Text(node, "name")?.Trim();
            var id = Text(node, "key") ?? Text(node, "id");
            var isRoot = depth == 0 && string.Equals(name, "Products", StringComparison.OrdinalIgnoreCase);
            var next = new List<string>(trail);
            if (!string.IsNullOrEmpty(name) && !isRoot)
            {
                next.Add(name);
            }

            if (id is not null && next.Count > 0)
            {
                paths[id] = string.Join(StoreListing.CategorySeparator, next);
            }

            if (node.TryGetProperty("children", out var children))
            {
                Walk(children, next, paths, depth + 1);
            }
        }
    }

    private static AldiPage ReadPage(string json, Dictionary<string, string> paths)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("no data list");
        }

        if (!root.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("pagination", out var pagination)
            || !pagination.TryGetProperty("totalCount", out var count) || !count.TryGetInt32(out var total))
        {
            throw new JsonException("no total count");
        }

        var products = new List<AldiProduct>();
        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && Text(element, "sku") is { Length: > 0 } sku)
            {
                products.Add(ReadProduct(element, sku, paths));
            }
        }

        return new AldiPage(products, total);
    }

    private static AldiProduct ReadProduct(JsonElement e, string sku, Dictionary<string, string> paths)
    {
        var name = Text(e, "name");
        var slug = Text(e, "urlSlugText");
        if (string.IsNullOrWhiteSpace(slug) || !Slug().IsMatch(slug))
        {
            slug = Slugify(name);
        }

        decimal? price = null;
        decimal? unit = null;
        string? basis = null;
        if (e.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            price = Cents(p, "amount");
            unit = Cents(p, "comparison");
            var display = Text(p, "comparisonDisplay");
            var slash = display?.IndexOf('/') ?? -1;
            basis = slash >= 0 ? display![(slash + 1)..].Trim() : null;
        }

        var was = ParseMoney(Text(e, "wasPriceDisplay"));
        if (was is not null && price is not null && was <= price)
        {
            was = null;
        }

        var item = new CatalogItem(
            sku,
            $"https://{Store.Aldi.Host()}/product/{slug}-{sku}",
            name,
            Text(e, "brandName"),
            Text(e, "sellingSize"),
            unit,
            basis,
            null,
            CategoryOf(e, paths),
            ImageOf(e),
            price,
            was);
        return new AldiProduct(sku, e.TryGetProperty("discontinued", out var d) && d.ValueKind == JsonValueKind.True, item);
    }

    /// <summary>The deepest path of the product's categories the tree knows, else the names the product itself carries.</summary>
    private static string? CategoryOf(JsonElement e, Dictionary<string, string> paths)
    {
        if (!e.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        var names = new List<string>();
        foreach (var category in categories.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Text(category, "name") is { Length: > 0 } name && !string.Equals(name, "Products", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }

            if ((Text(category, "id") ?? Text(category, "key")) is { } id && paths.TryGetValue(id, out var path)
                && (best is null || path.Length > best.Length))
            {
                best = path;
            }
        }

        return best ?? (names.Count > 0 ? string.Join(StoreListing.CategorySeparator, names) : null);
    }

    /// <summary>The first picture, at 300 pixels wide: its address is a template with <c>{width}</c> in it.</summary>
    private static string? ImageOf(JsonElement e)
    {
        if (!e.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object && Text(asset, "url") is { Length: > 0 } url)
            {
                return url.Replace("{width}", "300", StringComparison.Ordinal);
            }
        }

        return null;
    }

    /// <summary>Aldi sends euro as whole cents (799 is 7.99).</summary>
    private static decimal? Cents(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var cents))
        {
            return null;
        }

        var euro = decimal.Round(cents / 100m, 2);
        return euro > 0 ? euro : null;
    }

    /// <summary>The first amount in a display such as "€2.49", "Was €2.49" or "€3.03/1 L".</summary>
    internal static decimal? ParseMoney(string? display)
    {
        if (display is null)
        {
            return null;
        }

        var match = Amount().Match(display);
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? decimal.Round(value, 2)
            : null;
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static string Slugify(string? name)
    {
        var slug = NonSlug().Replace((name ?? string.Empty).ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "product" : slug;
    }

    [GeneratedRegex(@"-(?<sku>\d{6,})$")]
    private static partial Regex SkuAtEnd();

    [GeneratedRegex(@"\d+(?:\.\d{1,2})?")]
    private static partial Regex Amount();

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
