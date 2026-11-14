using Basket.Api.Prices;

namespace Basket.Api.Plan;

/// <summary>
/// The cheapest two shops that together price every open line, with what each one is bought at. <see cref="Groups"/> are the shops
/// in the fixed order (Lidl, Aldi, Tesco, Dunnes) with their lines in walk order, dated prices and subtotals, the same shape as the
/// By shop split. <see cref="Total"/> adds one pack of each line at the shop with the lower unit price, a card price or a multi-buy counted.
/// </summary>
public sealed record PlanPairSplit(
    decimal Total,
    DateOnly? OldestSeenOn,
    IReadOnlyList<ByShopGroup> Groups);

/// <summary>
/// The plan's answer when no one shop covers the list: of every pair of shops, those that between them price every line, the one
/// with the lowest total. Pure and built only on the plan's items: whether a shop prices a line, at what, and the line shape all come
/// from <see cref="ByShopBuilder"/>, so a price is the same number on the Plan tab and in By shop. A line is bought at the shop with the
/// lower unit price (<see cref="PromoOffer.Rank"/>, the rule the plan's best shop and By shop's split use), the first shop in the
/// fixed order on a tie; the pair's total is the sum of the chosen offers' pack prices and the pair with the lowest total wins.
/// Nothing is guessed: a pair that leaves any line unpriced is not a candidate, and neither is one where both shops price a line
/// but their unit prices cannot be compared (no pack size, or another unit), because choosing between them would be a guess. So
/// with a line no shop prices there is no split.
/// </summary>
public static class PairSplitBuilder
{
    public static PlanPairSplit? Build(IReadOnlyList<PlanItem> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var shops = Stores.All.ToList();
        PlanPairSplit? cheapest = null;
        for (var first = 0; first < shops.Count; first++)
        {
            for (var second = first + 1; second < shops.Count; second++)
            {
                var split = Pair(items, shops[first], shops[second]);
                if (split is not null && (cheapest is null || split.Total < cheapest.Total))
                {
                    cheapest = split;
                }
            }
        }

        return cheapest;
    }

    private static PlanPairSplit? Pair(IReadOnlyList<PlanItem> items, Store first, Store second)
    {
        var lines = new Dictionary<Store, List<ByShopLine>> { [first] = [], [second] = [] };
        foreach (var item in items)
        {
            var at = new[] { first, second }
                .Select(shop => (Shop: shop, Entry: ByShopBuilder.Priced(item, shop.Slug())))
                .Where(c => c.Entry is not null)
                .ToList();
            if (at.Count == 0)
            {
                return null;
            }

            if (at.Count > 1 && at.Any(c => c.Entry!.Offer!.Rank is null))
            {
                return null;
            }

            // The lower unit price wins; on a tie the first shop in the fixed order, which `at` keeps.
            var pick = at.MinBy(c => c.Entry!.Offer!.Rank);
            var line = ByShopBuilder.Line(item, pick.Entry!);
            lines[pick.Shop].Add(line);
        }

        var groups = new[] { first, second }
            .Where(shop => lines[shop].Count > 0)
            .Select(shop => ByShopBuilder.Group(shop, lines[shop]))
            .ToList();
        var all = groups.SelectMany(g => g.Lines).ToList();
        return new PlanPairSplit(
            groups.Sum(g => g.Total),
            all.Min(l => l.ObservedOn),
            groups);
    }
}
