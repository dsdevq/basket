using Basket.Api.Prices;

namespace Basket.Api.Plan;

/// <summary>
/// One line bought at one shop: what the shop's usable offer charges for one pack (a card price or a multi-buy counts, as in the
/// plan), the deal behind it and the day the price was read.
/// </summary>
public sealed record ByShopLine(
    Guid ItemId,
    string Text,
    decimal Price,
    string Deal,
    bool IsPromo,
    string? Card,
    string? PackSize,
    decimal? UnitPrice,
    string? Unit,
    DateOnly ObservedOn);

/// <summary>
/// The whole list priced at one shop. <see cref="Total"/> adds only the <see cref="Priced"/> lines (fresh, in stock, current); a
/// line the shop cannot price is in <see cref="Missing"/> with its reason and adds nothing, so a total is never a guess. It is
/// <see cref="Complete"/> when nothing is missing. <see cref="PacksDiffer"/> counts the priced lines whose pack is not the same at
/// every shop that prices them (or not known), so the total is not like for like on them. <see cref="Cheapest"/> marks the lowest
/// total among the complete shops.
/// </summary>
public sealed record ByShopStore(
    string Store,
    string StoreName,
    int Priced,
    decimal Total,
    bool Complete,
    bool Cheapest,
    DateOnly? OldestSeenOn,
    int PromoLines,
    int PacksDiffer,
    IReadOnlyList<string> Cards,
    IReadOnlyList<PlanBlocker> Missing);

/// <summary>The lines bought at one shop in the split, with what they cost together.</summary>
public sealed record ByShopGroup(string Store, string StoreName, decimal Total, IReadOnlyList<ByShopLine> Lines);

/// <summary>
/// Every line at the shop the plan names best for it (lowest unit price), shop by shop. A line the plan names no best shop for is
/// <see cref="Unplaced"/> with the plan's flag as the reason, never given to a shop by guess. <see cref="Complete"/> when none is.
/// <see cref="VersusCheapest"/> is the split's total minus the cheapest complete shop's (negative: the split is cheaper), set only
/// when the split is complete and a complete shop exists. The split picks by value, so it can cost more than one shop whose packs
/// are smaller.
/// </summary>
public sealed record ByShopSplit(
    decimal Total,
    bool Complete,
    decimal? VersusCheapest,
    IReadOnlyList<ByShopGroup> Groups,
    IReadOnlyList<PlanBlocker> Unplaced);

public sealed record ByShopResponse(
    DateOnly AsOf,
    int StaleAfterDays,
    int LineCount,
    IReadOnlyList<ByShopStore> Stores,
    ByShopSplit Split);

/// <summary>
/// Sums the plan per shop. Pure and built only on <see cref="PlanResponse"/>: which shops price a line, at what, and which one is
/// best come from the plan's own rule, so nothing is priced twice and the two screens cannot disagree.
/// </summary>
public static class ByShopBuilder
{
    public static ByShopResponse Build(PlanResponse plan)
    {
        var stores = Stores.All.Select(store => Store(store.Slug(), store.DisplayName(), plan.Items)).ToList();

        var cheapest = stores.Where(s => s.Complete).OrderBy(s => s.Total).Select(s => s.Store).FirstOrDefault();
        if (cheapest is not null)
        {
            stores = stores.Select(s => s.Store == cheapest ? s with { Cheapest = true } : s).ToList();
        }

        var split = Split(plan.Items, stores.FirstOrDefault(s => s.Cheapest));
        return new ByShopResponse(plan.AsOf, plan.StaleAfterDays, plan.Items.Count, stores, split);
    }

    private static ByShopStore Store(string slug, string name, IReadOnlyList<PlanItem> items)
    {
        var priced = new List<(PlanItem Item, PlanStorePrice Entry)>();
        var missing = new List<PlanBlocker>();
        foreach (var item in items)
        {
            var entry = item.Stores.FirstOrDefault(s => s.Store == slug);
            if (entry is { State: PlanStoreStates.Priced, Offer: not null })
            {
                priced.Add((item, entry));
            }
            else
            {
                missing.Add(new PlanBlocker(item.Id, item.Text, MissingReason(item, entry)));
            }
        }

        var seen = priced.Select(p => p.Entry.ObservedOn).OfType<DateOnly>().ToList();
        return new ByShopStore(
            slug,
            name,
            priced.Count,
            priced.Sum(p => p.Entry.Offer!.EachPrice),
            items.Count > 0 && missing.Count == 0,
            false,
            seen.Count > 0 ? seen.Min() : null,
            priced.Count(p => p.Entry.Offer!.IsPromo),
            priced.Count(p => PackDiffers(p.Item)),
            priced.Select(p => p.Entry.Offer!.Card).OfType<string>().Distinct().Order().ToList(),
            missing);
    }

    /// <summary>The line's own reason when it is free text; otherwise what this shop says about it, or that it has no page for it.</summary>
    private static string MissingReason(PlanItem item, PlanStorePrice? entry) =>
        item.Flag == PlanFlags.Unmatched ? PlanFlags.Unmatched : entry?.State ?? PlanStoreStates.NotLinked;

    /// <summary>Several shops price the line and their packs are not all the same known size.</summary>
    private static bool PackDiffers(PlanItem item)
    {
        var packs = item.Stores
            .Where(s => s.State == PlanStoreStates.Priced && s.Offer is not null)
            .Select(s => s.Offer!.PackSize)
            .ToList();
        return packs.Count > 1 && (packs.Any(p => p is null) || packs.Distinct().Count() > 1);
    }

    private static ByShopSplit Split(IReadOnlyList<PlanItem> items, ByShopStore? cheapest)
    {
        var groups = new List<ByShopGroup>();
        var unplaced = new List<PlanBlocker>();
        foreach (var store in Stores.All)
        {
            var slug = store.Slug();
            var lines = items
                .Where(i => i.BestStore == slug)
                .Select(i => Line(i, i.Stores.Single(s => s.Store == slug)))
                .ToList();
            if (lines.Count > 0)
            {
                groups.Add(new ByShopGroup(slug, store.DisplayName(), lines.Sum(l => l.Price), lines));
            }
        }

        foreach (var item in items.Where(i => i.BestStore is null))
        {
            unplaced.Add(new PlanBlocker(item.Id, item.Text, item.Flag ?? PlanFlags.NoPrice));
        }

        var total = groups.Sum(g => g.Total);
        var complete = items.Count > 0 && unplaced.Count == 0;
        return new ByShopSplit(total, complete, complete && cheapest is not null ? total - cheapest.Total : null, groups, unplaced);
    }

    private static ByShopLine Line(PlanItem item, PlanStorePrice entry)
    {
        var offer = entry.Offer!;
        return new ByShopLine(item.Id, item.Text, offer.EachPrice, offer.Deal, offer.IsPromo, offer.Card, offer.PackSize, offer.UnitPrice, offer.Unit, offer.ObservedOn);
    }
}
