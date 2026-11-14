using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Basket.Api.LidlPlus;

/// <summary>
/// Reads the purchased lines out of the printed receipt Lidl sends with a receipt (<c>htmlPrintedReceipt</c>), the only place the
/// lines are: a <c>&lt;pre&gt;</c> of <c>&lt;span&gt;</c>s where an article is <c>class="article"</c> with its name, shelf price and
/// (when more than one) quantity in <c>data-</c> attributes, and a discount is <c>class="discount"</c> with its amount as text. The
/// reader is tolerant on purpose: a line it cannot read is left out, never an error for the whole receipt.
/// </summary>
public static partial class LidlPrintedReceipt
{
    public static IReadOnlyList<LidlReceiptLine> ReadLines(string html)
    {
        var lines = new List<Line>();
        Line? current = null;

        foreach (Match tag in SpanRegex().Matches(html))
        {
            var attributes = ReadAttributes(tag.Groups[1].Value);
            var classes = attributes.TryGetValue("class", out var classText)
                ? classText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : [];
            var text = WebUtility.HtmlDecode(tag.Groups[2].Value).Trim();

            if (classes.Contains("article", StringComparer.OrdinalIgnoreCase))
            {
                var artId = attributes.GetValueOrDefault("data-art-id");

                // A line bought more than once is printed twice: the article with its quantity, then a "3 x 1.49" line of the same
                // article. The second is the first one's detail, not another line.
                if (current is not null && artId is not null && artId == current.ArtId && DetailRegex().Match(text) is { Success: true } detail)
                {
                    current.Quantity ??= LidlNumbers.Parse(detail.Groups[1].Value);
                    current.UnitPrice ??= LidlNumbers.Parse(detail.Groups[2].Value);
                    continue;
                }

                current = null;
                var name = WebUtility.HtmlDecode(attributes.GetValueOrDefault("data-art-description") ?? string.Empty).Trim();
                var unitPrice = LidlNumbers.Parse(attributes.GetValueOrDefault("data-unit-price"));
                if (name.Length == 0 || unitPrice is null)
                {
                    continue;
                }

                current = new Line(artId, name) { UnitPrice = unitPrice, Quantity = LidlNumbers.Parse(attributes.GetValueOrDefault("data-art-quantity")) };
                lines.Add(current);
            }
            else if (classes.Contains("discount", StringComparer.OrdinalIgnoreCase))
            {
                // A discount is taken off the article printed just before it. One with nothing to apply to, or no readable amount, is left out.
                if (current is not null && DiscountRegex().Match(text) is { Success: true } amount && LidlNumbers.Parse(amount.Groups[1].Value) is { } value)
                {
                    current.Discount -= Math.Abs(value);
                }
            }
        }

        return lines.Select(l => l.ToReceiptLine()).Where(l => l is not null).Select(l => l!).ToList();
    }

    private static Dictionary<string, string> ReadAttributes(string attributeText)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex().Matches(attributeText))
        {
            attributes.TryAdd(attribute.Groups[1].Value, attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Value);
        }

        return attributes;
    }

    private sealed class Line(string? artId, string description)
    {
        public string? ArtId { get; } = artId;

        public decimal? Quantity { get; set; }

        public decimal? UnitPrice { get; set; }

        /// <summary>What discounts took off this line; zero or negative.</summary>
        public decimal Discount { get; set; }

        public LidlReceiptLine? ToReceiptLine()
        {
            var quantity = Quantity ?? 1m;
            if (UnitPrice is not { } unitPrice || quantity <= 0)
            {
                return null;
            }

            // A fractional quantity is a weight in kilos and the unit price a kilo's; a whole one is a count.
            var isWeight = quantity != decimal.Truncate(quantity);
            return new LidlReceiptLine(description, quantity, isWeight, unitPrice, Math.Round((unitPrice * quantity) + Discount, 2, MidpointRounding.AwayFromZero));
        }
    }

    // A span's opening tag (quote-aware, so a ">" inside an attribute value does not end it) and the text up to the next tag.
    [GeneratedRegex("""<span\b((?:[^>"']|"[^"]*"|'[^']*')*)>([^<]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpanRegex();

    [GeneratedRegex("""([\w:-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();

    // "3 x 1.49" or "0,612 kg x 1,69 EUR/kg": a quantity, a multiplication sign, a price, and at most a short unit after each.
    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)\s*[a-zA-Z]{0,3}\s*[x×]\s*(\d+(?:[.,]\d+)?)(?:\s*[^\s\d]{1,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DetailRegex();

    // The amount closing a discount line: "Lidl Plus coupon -1.50" (or "1.50-").
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*-?$", RegexOptions.CultureInvariant)]
    private static partial Regex DiscountRegex();
}

/// <summary>Numbers as Lidl writes them: a plain number, or text with a decimal point or comma (<c>"1,49"</c>, <c>"1.234,56"</c>, <c>"-9.99"</c>).</summary>
internal static class LidlNumbers
{
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.Contains(',', StringComparison.Ordinal))
        {
            // A comma is the decimal mark; a dot beside it is a thousands separator.
            text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
