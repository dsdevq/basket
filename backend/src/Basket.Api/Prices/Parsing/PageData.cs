using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Basket.Api.Prices.Parsing;

/// <summary>Small readers shared by the shop parsers: JSON-LD blocks, attribute payloads, prices, dates.</summary>
internal static partial class PageData
{
    /// <summary>The schema.org Product object of a page, if it has one (shops put it in a JSON-LD script).</summary>
    public static JsonElement? ProductJsonLd(string html)
    {
        foreach (Match block in JsonLdBlock().Matches(html))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(block.Groups["json"].Value);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = document.RootElement;
            var candidates = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
            foreach (var candidate in candidates)
            {
                if (candidate.ValueKind == JsonValueKind.Object
                    && candidate.TryGetProperty("@type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "Product")
                {
                    return candidate.Clone();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The category path of a page, read from its schema.org BreadcrumbList: the crumbs between the shop's front page and the
    /// product itself, joined with <see cref="Basket.Api.Data.StoreListing.CategorySeparator"/> ("Chilled Food &gt; Milk").
    /// The leading "Home" crumb is dropped, and so is the last crumb when it is the product (its name, or its address, is the
    /// page's). Null when the page has no breadcrumb or nothing is left of it.
    /// </summary>
    public static string? BreadcrumbCategory(string html, string? productName = null, string? productUrl = null)
    {
        foreach (Match block in JsonLdBlock().Matches(html))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(block.Groups["json"].Value);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = document.RootElement;
            var candidates = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
            foreach (var candidate in candidates)
            {
                if (candidate.ValueKind != JsonValueKind.Object
                    || String(candidate, "@type") != "BreadcrumbList"
                    || !candidate.TryGetProperty("itemListElement", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var crumbs = list.EnumerateArray()
                    .Select(e => (Name: Clean(String(e, "name") ?? String(e, "item", "name")), Item: String(e, "item") ?? String(e, "item", "@id")))
                    .Where(c => c.Name is not null)
                    .ToList();
                if (crumbs.Count > 0 && IsFrontPage(crumbs[0].Item, crumbs[0].Name!))
                {
                    crumbs.RemoveAt(0);
                }

                if (crumbs.Count > 0
                    && ((productName is not null && string.Equals(crumbs[^1].Name, productName, StringComparison.OrdinalIgnoreCase))
                        || SamePath(crumbs[^1].Item, productUrl)))
                {
                    crumbs.RemoveAt(crumbs.Count - 1);
                }

                var path = string.Join(Basket.Api.Data.StoreListing.CategorySeparator, crumbs.Select(c => c.Name));
                return path.Length == 0 ? null : path;
            }
        }

        return null;
    }

    private static bool SamePath(string? first, string? second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a)
        && Uri.TryCreate(second, UriKind.Absolute, out var b)
        && a.AbsolutePath != "/"
        && a.AbsolutePath == b.AbsolutePath;

    private static bool IsFrontPage(string? item, string name) =>
        string.Equals(name, "Home", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(item, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/");

    /// <summary>The first Offer of a Product (the shops use either one object or a list).</summary>
    public static JsonElement? FirstOffer(JsonElement product)
    {
        if (!product.TryGetProperty("offers", out var offers))
        {
            return null;
        }

        return offers.ValueKind switch
        {
            JsonValueKind.Object => offers,
            JsonValueKind.Array when offers.GetArrayLength() > 0 => offers[0],
            _ => null,
        };
    }

    /// <summary>The JSON objects held in <c>data-grid-data="..."</c> attributes (Lidl's product tiles).</summary>
    public static IEnumerable<JsonElement> GridTiles(string html)
    {
        foreach (Match attribute in GridDataAttribute().Matches(html))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(WebUtility.HtmlDecode(attribute.Groups["data"].Value));
            }
            catch (JsonException)
            {
                continue;
            }

            yield return document.RootElement.Clone();
        }
    }

    public static string? String(JsonElement parent, params string[] path)
    {
        var node = Walk(parent, path);
        return node is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    /// <summary>A price the shops write as a number or as a numeric string; anything else (zero, negative) is no price.</summary>
    public static decimal? Money(JsonElement parent, params string[] path)
    {
        var node = Walk(parent, path);
        decimal? value = node switch
        {
            { ValueKind: JsonValueKind.Number } number => number.GetDecimal(),
            { ValueKind: JsonValueKind.String } text
                when decimal.TryParse(text.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        return value is > 0 and < 100_000 ? decimal.Round(value.Value, 2) : null;
    }

    /// <summary>
    /// The calendar day of a Lidl stamp, from Unix seconds or an ISO-8601 timestamp. Lidl writes a day boundary as
    /// <c>T23:59:59Z</c>, so the date is read in the stamp's own zone (UTC for Unix seconds) and never moved to Dublin time.
    /// </summary>
    public static DateOnly? CalendarDate(JsonElement parent, params string[] path)
    {
        var moment = Walk(parent, path) switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var seconds) && seconds > 0
                => DateTimeOffset.FromUnixTimeSeconds(seconds),
            { ValueKind: JsonValueKind.String } text
                when DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                => parsed,
            _ => (DateTimeOffset?)null,
        };

        return moment is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
    }

    /// <summary>
    /// The first usable photo address of a value that holds one: a string, an array of strings, or an object with a
    /// <c>url</c> (the three shapes schema.org <c>image</c> takes). Anything that is not a plain https address is skipped.
    /// </summary>
    public static string? ImageUrl(JsonElement? image)
    {
        switch (image)
        {
            case { ValueKind: JsonValueKind.String } text:
                return ImageUrls.TryNormalize(WebUtility.HtmlDecode(text.GetString()), out var url) ? url : null;
            case { ValueKind: JsonValueKind.Array } list:
                return list.EnumerateArray().Select(item => ImageUrl(item)).FirstOrDefault(found => found is not null);
            case { ValueKind: JsonValueKind.Object } obj when obj.TryGetProperty("url", out var inner):
                return ImageUrl(inner);
            default:
                return null;
        }
    }

    /// <summary>The page's <c>og:image</c> address (what shops give link previews), when it is a usable one.</summary>
    public static string? OgImage(string html)
    {
        foreach (Match tag in MetaTag().Matches(html))
        {
            var attributes = tag.Value;
            var property = MetaProperty().Match(attributes);
            if (!property.Success || !string.Equals(property.Groups["v"].Value, "og:image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = MetaContent().Match(attributes);
            if (content.Success && ImageUrls.TryNormalize(WebUtility.HtmlDecode(content.Groups["v"].Value), out var url))
            {
                return url;
            }
        }

        return null;
    }

    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var collapsed = Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        return collapsed.Length == 0 ? null : collapsed.Length > 200 ? collapsed[..200] : collapsed;
    }

    private static JsonElement? Walk(JsonElement node, string[] path)
    {
        var current = node;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                return null;
            }
        }

        return current;
    }

    [GeneratedRegex(@"<script[^>]*type=""application/ld\+json""[^>]*>(?<json>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdBlock();

    [GeneratedRegex(@"\bproperty\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaProperty();

    [GeneratedRegex(@"\bcontent\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaContent();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"data-grid-data=""(?<data>[^""]*)""")]
    private static partial Regex GridDataAttribute();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
