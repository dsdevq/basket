using System.Globalization;
using System.Text.RegularExpressions;

namespace Basket.Api.Prices.Promotions;

/// <summary>
/// A "buy several" offer: <see cref="Quantity"/> items at once. Either they cost <see cref="Total"/> together ("2 for
/// €4"), or you pay for <see cref="PayFor"/> of them and the rest are free ("3 for 2", "buy 1 get 1 free").
/// </summary>
public sealed record MultiBuy(int Quantity, decimal? Total, int? PayFor);

/// <summary>Reads the shop's promo wording. Only the shapes below are understood; anything else is left alone, never guessed.</summary>
public static partial class PromoText
{
    private const string Number = @"(?:\d+\.\d+|\d+,\d{1,2}(?!\d)|\d+)";

    private static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
    };

    /// <summary>
    /// "2 for €4" and "Mix 'n' Match 2 for 4.50" (money), "3 for 2" (pay for two of three), "Buy 1 get 1 free", "BOGOF".
    /// Without a € sign a whole number after "for" is read as a count of items to pay for when it is smaller than the
    /// quantity ("3 for 2") and as euros when it is larger ("2 for 3" is €3); "2 for 2" says nothing and is refused.
    /// </summary>
    public static MultiBuy? ParseMultiBuy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (BuyGetFree().Match(text) is { Success: true } free
            && Count(free.Groups["buy"].Value) is { } buy and > 0
            && Count(free.Groups["get"].Value) is { } get and > 0)
        {
            return new MultiBuy(buy + get, null, buy);
        }

        if (Bogof().IsMatch(text))
        {
            return new MultiBuy(2, null, 1);
        }

        var match = ForPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var quantity = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        var amount = decimal.Parse(match.Groups["m"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var isMoney = match.Groups["eur"].Success || match.Groups["m"].Value.Contains('.') || match.Groups["m"].Value.Contains(',');
        if (quantity < 2 || amount <= 0)
        {
            return null;
        }

        if (isMoney)
        {
            return new MultiBuy(quantity, amount, null);
        }

        if (amount < quantity)
        {
            return new MultiBuy(quantity, null, (int)amount);
        }

        return amount > quantity ? new MultiBuy(quantity, amount, null) : null;
    }

    /// <summary>"20% off", "Save 15%": the percentage the shop claims. It is shown, never applied on top of the shown price.</summary>
    public static decimal? ParsePercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = PercentOff().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Value;
        return decimal.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent) && percent is > 0 and <= 100
            ? percent
            : null;
    }

    private static int? Count(string value) =>
        Words.TryGetValue(value, out var word) ? word
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
        : null;

    [GeneratedRegex(@"\bbuy\s+(?<buy>\d+|one|two|three|four)\s+get\s+(?<get>\d+|one|two|three|four)\s+(?:items?\s+)?free\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuyGetFree();

    [GeneratedRegex(@"\bbogof\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bogof();

    [GeneratedRegex($@"(?<![\w.,€])(?<n>\d{{1,2}})\s+for\s+(?<eur>€\s*)?(?<m>{Number})(?![\d%])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForPattern();

    [GeneratedRegex($@"(?<a>{Number})\s*%\s*(?:off|discount)|\bsave\s+(?<b>{Number})\s*%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentOff();
}
