using System.Globalization;
using System.Text.RegularExpressions;

namespace Basket.Api.Prices.Promotions;

/// <summary>What a pack is measured in. Prices are only compared inside one kind: a kilo of mince is not a litre of milk.</summary>
public enum UnitKind
{
    Mass,
    Volume,
    Piece,
}

/// <summary>
/// How much a pack holds, in the unit its price is compared per: kilograms, litres or pieces. <see cref="Quantity"/> is
/// always in that base unit (500 g is 0.5).
/// </summary>
public readonly record struct PackSize(decimal Quantity, UnitKind Kind)
{
    /// <summary>The unit a price is quoted per: <c>kg</c>, <c>l</c> or <c>each</c>.</summary>
    public string Unit => Kind switch
    {
        UnitKind.Mass => "kg",
        UnitKind.Volume => "l",
        _ => "each",
    };

    /// <summary>The size as a shopper reads it on a shelf: "500 g", "1.5 l", "6 pieces".</summary>
    public string Label => Kind switch
    {
        UnitKind.Mass when Quantity < 1 => $"{Format(Quantity * 1000)} g",
        UnitKind.Mass => $"{Format(Quantity)} kg",
        UnitKind.Volume when Quantity < 1 => $"{Format(Quantity * 1000)} ml",
        UnitKind.Volume => $"{Format(Quantity)} l",
        _ => Quantity == 1 ? "1 piece" : $"{Format(Quantity)} pieces",
    };

    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads a pack size out of the words a shop (or the household) uses for a product. basket collects no size field, so
/// the name is the only source: "Clonbawn Protein Milk 1L", "6 x 330ml", "Pack of 6". Nothing is guessed: a name with no
/// size gives <c>null</c>, and the offer is shown without a unit price instead.
/// </summary>
public static partial class PackSizes
{
    private const string Number = @"(?:\d+\.\d+|\d+,\d{1,2}(?!\d)|\d+)";
    private const string Units = @"kg|ml|cl|ltrs?|litres?|liters?|l|g";
    private const decimal MaxQuantity = 100_000m;

    /// <summary>
    /// The first of <paramref name="texts"/> that holds a size, with where it came from (its index). A text that carries a
    /// size signal but is ambiguous ends the search: a later text is never read in its place.
    /// </summary>
    public static (PackSize Size, int Source)? Read(params string?[] texts)
    {
        for (var i = 0; i < texts.Length; i++)
        {
            var (size, ambiguous) = Inspect(texts[i]);
            if (ambiguous)
            {
                return null;
            }

            if (size is { } found)
            {
                return (found, i);
            }
        }

        return null;
    }

    /// <summary>
    /// A multipack ("6 x 330ml", "4x125g") counts all its parts; otherwise the first weight or volume wins ("1L"), then a
    /// piece count ("pack of 6", "12 pack", "each"). A weight or volume next to a separate pack count ("415g x 4",
    /// "330ml 6 pack") is ambiguous, so it gives <c>null</c>.
    /// </summary>
    public static PackSize? Parse(string? text) => Inspect(text).Size;

    private static (PackSize? Size, bool Ambiguous) Inspect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, false);
        }

        if (Multipack().Match(text) is { Success: true } multi
            && Measure(multi.Groups["qty"].Value, multi.Groups["unit"].Value) is { } part
            && int.Parse(multi.Groups["count"].Value, CultureInfo.InvariantCulture) is var count and > 0)
        {
            return (Valid(new PackSize(part.Quantity * count, part.Kind)), false);
        }

        if (Single().Match(text) is { Success: true } single
            && Measure(single.Groups["qty"].Value, single.Groups["unit"].Value) is { } measured)
        {
            return PackOf().IsMatch(text) || MeasureThenCount().IsMatch(text) ? (null, true) : (Valid(measured), false);
        }

        if (PackOf().Match(text) is { Success: true } pack)
        {
            var value = pack.Groups["n"].Success ? pack.Groups["n"].Value : pack.Groups["m"].Value;
            return (Valid(new PackSize(decimal.Parse(value, CultureInfo.InvariantCulture), UnitKind.Piece)), false);
        }

        return (Each().IsMatch(text) ? new PackSize(1, UnitKind.Piece) : null, false);
    }

    private static PackSize? Valid(PackSize size) => size.Quantity > 0 && size.Quantity <= MaxQuantity ? size : null;

    /// <summary>A number and its unit in the base unit: 500 g is 0.5 kg, 75 cl is 0.75 l.</summary>
    private static PackSize? Measure(string number, string unit)
    {
        if (!decimal.TryParse(number.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity))
        {
            return null;
        }

        return unit.ToLowerInvariant() switch
        {
            "kg" => new PackSize(quantity, UnitKind.Mass),
            "g" => new PackSize(quantity / 1000m, UnitKind.Mass),
            "l" or "ltr" or "ltrs" or "litre" or "litres" or "liter" or "liters" => new PackSize(quantity, UnitKind.Volume),
            "ml" => new PackSize(quantity / 1000m, UnitKind.Volume),
            "cl" => new PackSize(quantity / 100m, UnitKind.Volume),
            _ => null,
        };
    }

    [GeneratedRegex($@"(?<![\w.,])(?<count>\d{{1,3}})\s*[x×]\s*(?<qty>{Number})\s*(?<unit>{Units})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Multipack();

    [GeneratedRegex($@"(?<![\w.,])(?<qty>{Number})\s*(?<unit>{Units})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Single();

    [GeneratedRegex($@"(?<![\w.,]){Number}\s*(?:{Units})\s*[x×]\s*\d{{1,3}}\b(?!\s*(?:{Units})\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeasureThenCount();

    [GeneratedRegex(@"\bpack\s+of\s+(?<n>\d{1,3})\b|(?<![\w.,])(?<m>\d{1,3})\s*-?\s*(?:pack|pk|pcs?|pieces?|count|ct)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackOf();

    [GeneratedRegex(@"\b(?:each|per\s+piece|single)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Each();
}
