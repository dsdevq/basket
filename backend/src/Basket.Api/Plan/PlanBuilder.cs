using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Promotions;

namespace Basket.Api.Plan;

/// <summary>Why a list line has no best store. Absent when it has one. Every value is a fact about the data, never a guess.</summary>
public static class PlanFlags
{
    /// <summary>The line is free text, or its product was deleted. It is never matched from the words.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>The product has no usable price at any shop: not linked, not read yet, no price listed, or the offer has not started or has ended.</summary>
    public const string NoPrice = "no_price";

    /// <summary>Every price the product has is older than <see cref="PromotionBuilder.StaleAfterDays"/> days.</summary>
    public const string Stale = "stale";

    /// <summary>Every fresh price belongs to a product the shop shows as out of stock.</summary>
    public const string OutOfStock = "out_of_stock";

    /// <summary>
    /// Several shops have a fresh price, but they cannot be set side by side (no pack size at one, or kilos against pieces),
    /// so none is named best. The prices are still listed, and each shop still counts as covering the line.
    /// </summary>
    public const string NotComparable = "not_comparable";
}

/// <summary>What one shop can say about a line. Only <see cref="Priced"/> counts towards covering the list.</summary>
public static class PlanStoreStates
{
    public const string Priced = "priced";
    public const string Stale = "stale";
    public const string OutOfStock = "out_of_stock";
    public const string NotRead = "not_read";
    public const string NoPrice = "no_price";
    public const string Upcoming = "upcoming";
    public const string Ended = "ended";

    /// <summary>A shop with no listing of the product has no entry at all; as a blocker's reason it is this.</summary>
    public const string NotLinked = "not_linked";
}

/// <summary>
/// One linked shop for one line. <see cref="Offer"/> is the normalised price (the promotions rule: the cheapest way to buy
/// it, per pack and per unit) when the shop has a usable reading, even a stale or out-of-stock one; the raw shelf
/// <see cref="Price"/>, <see cref="WasPrice"/> and <see cref="PromoText"/> are the reading itself.
/// </summary>
public sealed record PlanStorePrice(
    string Store,
    string StoreName,
    string State,
    DateOnly? ObservedOn,
    decimal? Price,
    decimal? WasPrice,
    string? PromoText,
    PromoOffer? Offer,
    bool Best);

/// <summary>
/// A price the household really paid at a shop, from a receipt, with the day it was paid. Not a shelf price: it never takes part in
/// choosing the best shop, it is the number to set the shelf prices against. A weighed product's is the price of a kilo.
/// </summary>
public sealed record PlanPaid(string Store, string StoreName, decimal Price, DateOnly PaidOn);

/// <summary>
/// One open line of the list. <see cref="BestStore"/> is the cheapest shop by unit price among the fresh, in-stock,
/// comparable prices; null with a <see cref="Flag"/> saying why when there is none. <see cref="LastPaid"/> is the latest receipt
/// price at each shop the product was bought at, whether or not that shop's page is linked.
/// </summary>
public sealed record PlanItem(
    Guid Id,
    string Text,
    Guid? ProductId,
    string? ProductName,
    string? PickedStore,
    string? Flag,
    string? BestStore,
    IReadOnlyList<PlanStorePrice> Stores,
    IReadOnlyList<PlanPaid> LastPaid);

/// <summary>A line the reference shop cannot price. <see cref="Reason"/> is the line's flag when no shop prices it, else the shop's own state.</summary>
public sealed record PlanBlocker(Guid ItemId, string Text, string Reason);

/// <summary>
/// Whether one shop prices every open line. <see cref="Store"/> is that shop when <see cref="SingleStore"/> (<see cref="CoveredBy"/>
/// lists all that do); otherwise <see cref="ClosestStore"/> is the shop pricing the most lines and <see cref="Blockers"/> the lines
/// it cannot price. When no one shop covers the list, <see cref="Split"/> is the cheapest two shops that together do (see
/// <see cref="PairSplitBuilder"/>), or null when no two shops can.
/// </summary>
public sealed record PlanCoverage(
    bool SingleStore,
    string? Store,
    IReadOnlyList<string> CoveredBy,
    string? ClosestStore,
    IReadOnlyList<PlanBlocker> Blockers,
    PlanPairSplit? Split = null);

public sealed record PlanResponse(DateOnly AsOf, int StaleAfterDays, IReadOnlyList<PlanItem> Items, PlanCoverage Coverage);

/// <summary>
/// Turns the open lines of the list and the latest reading of every linked shop page into the plan. Pure, like
/// <see cref="PromotionBuilder"/>, whose rule it reuses: a price is the same number on both screens.
/// </summary>
public static class PlanBuilder
{
    private static readonly string[] NoShopPrices = [PlanFlags.Unmatched, PlanFlags.NoPrice, PlanFlags.Stale, PlanFlags.OutOfStock];

    public static PlanResponse Build(
        IEnumerable<ListItem> lines,
        IReadOnlyDictionary<Guid, Product> products,
        ILookup<Guid, StoreListing> listings,
        IReadOnlyDictionary<Guid, PriceObservation> latest,
        DateOnly today,
        IReadOnlyDictionary<(Guid ProductId, Store Store), PaidPrice>? paid = null)
    {
        var items = lines.Select(line => Item(line, products, listings, latest, today, paid)).ToList();
        return new PlanResponse(today, PromotionBuilder.StaleAfterDays, items, Coverage(items));
    }

    private static PlanItem Item(
        ListItem line,
        IReadOnlyDictionary<Guid, Product> products,
        ILookup<Guid, StoreListing> listings,
        IReadOnlyDictionary<Guid, PriceObservation> latest,
        DateOnly today,
        IReadOnlyDictionary<(Guid ProductId, Store Store), PaidPrice>? paid)
    {
        var picked = line.Store?.Slug();
        if (line.ProductId is not { } productId || !products.TryGetValue(productId, out var product))
        {
            return new PlanItem(line.Id, line.Text, null, null, picked, PlanFlags.Unmatched, null, [], []);
        }

        var comparison = PromotionBuilder.Compare(product, listings, latest, today);
        var stores = Stores.All
            .Select(store => Entry(store, product, listings, latest, comparison))
            .OfType<PlanStorePrice>()
            .ToList();

        var (flag, best) = Choose(stores, comparison);
        if (best is not null)
        {
            stores = stores.Select(s => s.Store == best ? s with { Best = true } : s).ToList();
        }

        var lastPaid = Stores.All
            .Select(store => paid is not null && paid.TryGetValue((product.Id, store), out var row)
                ? new PlanPaid(store.Slug(), store.DisplayName(), row.Price, row.PaidOn)
                : null)
            .OfType<PlanPaid>()
            .ToList();
        return new PlanItem(line.Id, line.Text, product.Id, product.Name, picked, flag, best, stores, lastPaid);
    }

    private static PlanStorePrice? Entry(
        Store store,
        Product product,
        ILookup<Guid, StoreListing> listings,
        IReadOnlyDictionary<Guid, PriceObservation> latest,
        ProductComparison comparison)
    {
        var slug = store.Slug();
        var listing = listings[product.Id].FirstOrDefault(l => l.Store == store);
        if (listing is null)
        {
            return null;
        }

        latest.TryGetValue(listing.Id, out var reading);
        var offer = comparison.Offers.FirstOrDefault(o => o.Store == slug);
        var state = offer is not null
            ? offer.Flags.Contains(PromotionBuilder.FlagStale) ? PlanStoreStates.Stale
                : offer.Flags.Contains(PromotionBuilder.FlagOutOfStock) ? PlanStoreStates.OutOfStock
                : PlanStoreStates.Priced
            : comparison.Gaps.FirstOrDefault(g => g.Store == slug)?.Reason ?? PlanStoreStates.NotRead;
        return new PlanStorePrice(slug, store.DisplayName(), state, reading?.ObservedOn, reading?.Price, reading?.WasPrice, reading?.PromoText, offer, false);
    }

    /// <summary>
    /// The best shop for a line, or why there is none. Only fresh, in-stock prices compete. One such shop is best by default; with
    /// several, every one has to be comparable (<see cref="PromoOffer.Rank"/> set) or none is named, because picking the cheapest
    /// of the comparable ones would hide a shop whose price cannot be read against them.
    /// </summary>
    private static (string? Flag, string? Best) Choose(IReadOnlyList<PlanStorePrice> stores, ProductComparison comparison)
    {
        if (stores.All(s => s.Offer is null))
        {
            return (PlanFlags.NoPrice, null);
        }

        var fresh = stores.Where(s => s.State is PlanStoreStates.Priced or PlanStoreStates.OutOfStock).ToList();
        if (fresh.Count == 0)
        {
            return (PlanFlags.Stale, null);
        }

        var priced = fresh.Where(s => s.State == PlanStoreStates.Priced).ToList();
        if (priced.Count == 0)
        {
            return (PlanFlags.OutOfStock, null);
        }

        if (priced.Count == 1)
        {
            return (null, priced[0].Store);
        }

        if (priced.Any(s => s.Offer!.Rank is null))
        {
            return (PlanFlags.NotComparable, null);
        }

        // Equal unit prices share a place; the sort is stable and the shops come in their fixed order (Lidl, Aldi, Tesco, Dunnes), so the first of them is named.
        var first = priced.OrderBy(s => s.Offer!.Rank).First();
        return (null, first.Store);
    }

    private static PlanCoverage Coverage(IReadOnlyList<PlanItem> items)
    {
        var counts = Stores.All
            .Select(store => (Slug: store.Slug(), Count: items.Count(i => PricedAt(i, store.Slug()))))
            .ToList();
        var covering = items.Count == 0 ? [] : counts.Where(c => c.Count == items.Count).Select(c => c.Slug).ToList();
        if (covering.Count > 0)
        {
            var winner = covering
                .Select((slug, order) => (slug, order, wins: items.Count(i => i.BestStore == slug)))
                .OrderByDescending(c => c.wins)
                .ThenBy(c => c.order)
                .First().slug;
            return new PlanCoverage(true, winner, covering, null, []);
        }

        var closest = counts.Where(c => c.Count > 0).OrderByDescending(c => c.Count).Select(c => c.Slug).FirstOrDefault();
        var blockers = items
            .Where(i => closest is null || !PricedAt(i, closest))
            .Select(i => new PlanBlocker(i.Id, i.Text, Reason(i, closest)))
            .ToList();
        return new PlanCoverage(false, null, [], closest, blockers, PairSplitBuilder.Build(items));
    }

    private static bool PricedAt(PlanItem item, string store) =>
        item.Stores.Any(s => s.Store == store && s.State == PlanStoreStates.Priced);

    private static string Reason(PlanItem item, string? closest) =>
        item.Flag is { } flag && NoShopPrices.Contains(flag)
            ? flag
            : item.Stores.FirstOrDefault(s => s.Store == closest)?.State ?? PlanStoreStates.NotLinked;
}
