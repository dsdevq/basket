using Basket.Api.Data;

namespace Basket.Api.Prices.Promotions;

/// <summary>One dated price of one shop page: what a pack costs when the deal's terms are met (<see cref="Effective"/>), beside the shelf price and the offer text.</summary>
public sealed record HistoryPoint(
    DateOnly ObservedOn,
    decimal? Effective,
    decimal? Price,
    decimal? WasPrice,
    decimal? LoyaltyPrice,
    string? LoyaltyScheme,
    string? PromoText,
    bool InStock);

/// <summary>A price and the day it was seen.</summary>
public sealed record HistoryExtreme(decimal Price, DateOnly On);

/// <summary>
/// The last <see cref="PriceHistory.WindowDays"/> days of one shop page. <see cref="LowestIn90Days"/> says today's promo is the
/// cheapest of the window and was dearer before, the check that shows a "discount" is not a fake one.
/// </summary>
public sealed record PriceHistoryResponse(
    Guid ProductId,
    string Store,
    string StoreName,
    int Days,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<HistoryPoint> Points,
    HistoryExtreme? Lowest,
    HistoryExtreme? Highest,
    decimal? Current,
    bool LowestIn90Days);

/// <summary>
/// The price history rule, pure. A reading is reduced to its effective pack price by <see cref="OfferCalculator"/> (so a
/// "2 for €4" or a card price counts as what it costs you, the way the offer is ranked); readings with no price say nothing
/// and are left out. The window is the Dublin day and the 89 before it.
/// </summary>
public static class PriceHistory
{
    public const int WindowDays = 90;

    /// <summary>The first day of the window that ends on <paramref name="today"/>.</summary>
    public static DateOnly WindowStart(DateOnly today) => today.AddDays(1 - WindowDays);

    /// <summary>What a pack of this reading costs when its terms are met, or null when it has no price.</summary>
    public static decimal? Effective(PriceObservation o) =>
        OfferCalculator.Evaluate(new Reading(o.Price, o.WasPrice, o.LoyaltyPrice, o.LoyaltyScheme, o.PromoText))?.EachPrice;

    /// <summary>
    /// True when <paramref name="current"/> is no higher than any effective price of the window and the window holds a
    /// higher one. A price that has never been anything else is not "the lowest", it is just the price.
    /// </summary>
    public static bool IsLowest(decimal current, IEnumerable<PriceObservation> window, DateOnly today)
    {
        var start = WindowStart(today);
        var seen = window
            .Where(o => o.ObservedOn >= start && o.ObservedOn <= today)
            .Select(Effective)
            .OfType<decimal>()
            .ToList();
        return seen.Count > 0 && seen.Min() >= current && seen.Max() > current;
    }

    public static PriceHistoryResponse Build(
        Guid productId,
        Store store,
        IReadOnlyCollection<PriceObservation> observations,
        DateOnly today)
    {
        var start = WindowStart(today);
        var points = observations
            .Where(o => o.ObservedOn >= start && o.ObservedOn <= today)
            .OrderBy(o => o.ObservedOn)
            .Select(o => new HistoryPoint(o.ObservedOn, Effective(o), o.Price, o.WasPrice, o.LoyaltyPrice, o.LoyaltyScheme, o.PromoText, o.InStock))
            .ToList();
        var priced = points.Where(p => p.Effective is not null).ToList();
        var lowest = priced.Count == 0 ? null : priced.OrderBy(p => p.Effective).ThenByDescending(p => p.ObservedOn).First();
        var highest = priced.Count == 0 ? null : priced.OrderByDescending(p => p.Effective).ThenByDescending(p => p.ObservedOn).First();
        var current = priced.Count == 0 ? null : priced[^1].Effective;
        var latest = observations
            .Where(o => o.ObservedOn >= start && o.ObservedOn <= today)
            .OrderBy(o => o.ObservedOn)
            .LastOrDefault(o => Effective(o) is not null);
        var isPromo = latest is not null
            && OfferCalculator.Evaluate(new Reading(latest.Price, latest.WasPrice, latest.LoyaltyPrice, latest.LoyaltyScheme, latest.PromoText)) is { Kind: not DealKind.Regular };
        return new PriceHistoryResponse(
            productId,
            store.Slug(),
            store.DisplayName(),
            WindowDays,
            start,
            today,
            points,
            lowest is null ? null : new HistoryExtreme(lowest.Effective!.Value, lowest.ObservedOn),
            highest is null ? null : new HistoryExtreme(highest.Effective!.Value, highest.ObservedOn),
            current,
            isPromo && current is { } now && IsLowest(now, observations, today));
    }
}
