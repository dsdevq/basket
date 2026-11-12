using System.Globalization;
using System.Text.RegularExpressions;

namespace Basket.Api.Matching;

public enum SizeUnit
{
    /// <summary>Grams.</summary>
    Mass,

    /// <summary>Millilitres.</summary>
    Volume,

    /// <summary>Items (eggs, tea bags, slices).</summary>
    Count,
}

/// <summary>
/// How much of a product one pack holds, read from its name and reduced to a base unit so "1.5L", "1500 ml" and "150cl"
/// are the same size. <see cref="Packs"/> is the number of units in a multipack ("6 x 330ml" is 6 packs of 330 ml), so a
/// 6-pack and one 1980 ml bottle are not taken for the same thing.
/// </summary>
public sealed partial record ProductSize(SizeUnit Unit, decimal Amount, int Packs)
{
    /// <summary>The first size the text states, or null when it states none.</summary>
    public static ProductSize? Parse(string? text) => ParseWithSpan(text)?.Size;

    /// <summary>The text with the size expression removed, for comparing names apart from their sizes.</summary>
    public static string StripSize(string text)
    {
        var found = ParseWithSpan(text);
        return found is null ? text : string.Concat(text.AsSpan(0, found.Value.Start), " ", text.AsSpan(found.Value.Start + found.Value.Length));
    }

    private static (ProductSize Size, int Start, int Length)? ParseWithSpan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var measure = Measure().Match(text);
        if (measure.Success)
        {
            var amount = decimal.Parse(measure.Groups["amount"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var packs = measure.Groups["packs"].Success ? int.Parse(measure.Groups["packs"].Value, CultureInfo.InvariantCulture) : 1;
            var (unit, factor) = measure.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "kg" => (SizeUnit.Mass, 1000m),
                "g" or "gr" or "gm" => (SizeUnit.Mass, 1m),
                "l" or "lt" or "ltr" or "litre" or "litres" or "liter" or "liters" => (SizeUnit.Volume, 1000m),
                "cl" => (SizeUnit.Volume, 10m),
                _ => (SizeUnit.Volume, 1m),
            };
            return (new ProductSize(unit, decimal.Round(amount * factor, 2), packs), measure.Index, measure.Length);
        }

        var count = Items().Match(text);
        if (count.Success)
        {
            var number = count.Groups["n"].Value;
            return (new ProductSize(SizeUnit.Count, int.Parse(number, CultureInfo.InvariantCulture), 1), count.Index, count.Length);
        }

        return null;
    }

    // "500g", "1.5 L", "75cl", "6 x 330ml", "6x330 ml". Digits must not run into a longer number ("1500g" is not "500g").
    [GeneratedRegex(@"(?<![\d.,])(?:(?<packs>\d{1,2})\s*[x×]\s*)?(?<amount>\d+(?:[.,]\d+)?)\s*(?<unit>kg|gr|gm|g|litres?|liters?|ltr|lt|l|cl|ml)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Measure();

    // "10 pack", "pack of 6", "12 pieces", "6 eggs"-style counts are written with a noun after the number, or "pack of".
    [GeneratedRegex(@"(?<![\d.,])(?:(?<n>\d{1,3})\s*(?:pack|pk|pcs|pieces|count|ct|slices|bags|rolls)(?![a-z])|(?:pack|pk) of\s*(?<n>\d{1,3})(?!\d))", RegexOptions.IgnoreCase)]
    private static partial Regex Items();
}
