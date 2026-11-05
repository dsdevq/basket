using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Basket.Api.Prices.Parsing;

/// <summary>Small readers shared by the shop parsers: JSON-LD blocks, attribute payloads, prices, dates.</summary>
internal static partial class PageData
{
    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

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

    public static DateOnly? DublinDate(JsonElement parent, params string[] path)
    {
        var node = Walk(parent, path);
        if (node is not { ValueKind: JsonValueKind.Number } number || !number.TryGetInt64(out var seconds) || seconds <= 0)
        {
            return null;
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), Dublin).DateTime);
    }

    /// <summary>The "€1.49" inside an aria-label or element text.</summary>
    public static decimal? EuroAmount(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = Euro().Match(text);
        return match.Success
            && decimal.TryParse(match.Groups["amount"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            && amount > 0
            ? amount
            : null;
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

    [GeneratedRegex(@"data-grid-data=""(?<data>[^""]*)""")]
    private static partial Regex GridDataAttribute();

    [GeneratedRegex(@"€\s*(?<amount>\d+(?:[.,]\d{1,2})?)")]
    private static partial Regex Euro();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
