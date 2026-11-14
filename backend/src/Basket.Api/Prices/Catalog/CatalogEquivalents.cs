using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices.Promotions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Catalog;

/// <summary>
/// How an offer compares per unit. <see cref="UnitPrice"/> is euro per <see cref="Unit"/> (<c>kg</c>, <c>l</c> or <c>each</c>), worked
/// out from the row's own parsed size and its price today, so two shops' figures are in one unit whatever each prints on its label;
/// null when the row has no size, no usable price, or <see cref="Flags"/> says otherwise. <see cref="Difference"/> is this unit price
/// minus the source product's (negative means cheaper than the source); it is set only when both are comparable.
/// </summary>
public sealed record CatalogComparison(string? Unit, string? Pack, decimal? UnitPrice, decimal? Difference, IReadOnlyList<string> Flags);

/// <summary>
/// One catalog product in the equivalents answer: the row as a tile shows it, why the matcher proposed it (<see cref="Basis"/> and
/// <see cref="Score"/>, both null for the source itself) and its comparison.
/// </summary>
public sealed record CatalogEquivalentItem(CatalogItemResponse Item, string? Basis, int? Score, CatalogComparison Comparison);

/// <summary>The best equivalents in one shop, best first; empty when the shop has none.</summary>
public sealed record StoreEquivalents(string Store, string StoreName, IReadOnlyList<CatalogEquivalentItem> Items);

/// <summary>
/// <see cref="Reason"/> is set when nothing could be looked up: <c>no_size_or_barcode</c> means the product states neither a size nor
/// a barcode, so there is nothing safe to narrow a catalog by (the lists are then empty, not "no equivalent exists").
/// </summary>
public sealed record CatalogEquivalentsResponse(CatalogEquivalentItem Product, string? Reason, IReadOnlyList<StoreEquivalents> Stores);

/// <summary>
/// The equivalents of one catalog product in the other shops: the rows of each other shop's catalog that <see cref="CatalogRows"/>
/// narrows and <see cref="CandidateMatcher"/> accepts, the best <see cref="PerStore"/> of each with a unit price beside the source's.
/// Read only: the same matcher the server's candidate search runs, but nothing is proposed, linked or stored, and a person's
/// confirmed or rejected matches are not consulted (this is browsing the catalog, not the shelf). Pure of side effects, so asking again
/// gives the same answer until a sweep changes the catalog.
/// </summary>
public static class CatalogEquivalents
{
    /// <summary>Most equivalents answered per shop: a screen of rows for each shop on a phone.</summary>
    public const int PerStore = 3;

    public const string FlagStale = "stale";
    public const string FlagOutOfStock = "out_of_stock";
    public const string FlagNoPrice = "no_price";
    public const string FlagNoSize = "no_size";
    public const string FlagOtherUnit = "other_unit";

    public const string ReasonNoSizeOrBarcode = "no_size_or_barcode";

    public static async Task<CatalogEquivalentsResponse> BuildAsync(AppDbContext db, StoreProduct source, DateOnly today, CancellationToken ct)
    {
        var facts = CatalogRows.FactsOf(source);
        var searchable = Gtin.TryNormalize(facts.Barcode, out _)
            || ((facts.Size ?? ProductSize.Parse(facts.Name)) is not null && CandidateMatcher.KeyWord(facts.Name) is not null);

        var offered = new List<(StoreProduct Row, MatchVerdict Verdict)>();
        if (searchable)
        {
            foreach (var store in Stores.All.Where(s => s != source.Store))
            {
                foreach (var row in await CatalogRows.NarrowAsync(db, store, facts, ct))
                {
                    var verdict = CandidateMatcher.Evaluate(facts, new CandidateFacts(CatalogRows.TitleOf(row), row.Barcode, row.Category, CatalogRows.SizeOf(row)));
                    if (verdict is not null)
                    {
                        offered.Add((row, verdict));
                    }
                }
            }
        }

        var ids = offered.Select(o => o.Row.Id).Append(source.Id).ToList();
        var prices = await CatalogEndpoints.LatestPricesAsync(db, ids, ct);
        var sourceCompare = Compare(source, prices.GetValueOrDefault(source.Id), today, null);

        var stores = Stores.All.Where(s => s != source.Store).Select(store =>
        {
            var items = offered
                .Where(o => o.Row.Store == store)
                .Select(o => (o.Row, o.Verdict, Price: prices.GetValueOrDefault(o.Row.Id), Compare: Compare(o.Row, prices.GetValueOrDefault(o.Row.Id), today, sourceCompare)))
                .OrderByDescending(o => o.Verdict.Score)
                .ThenBy(o => o.Compare.UnitPrice is null || o.Compare.Flags.Contains(FlagStale) || o.Compare.Flags.Contains(FlagOutOfStock))
                .ThenBy(o => o.Compare.UnitPrice)
                .ThenBy(o => o.Row.Name, StringComparer.Ordinal)
                .ThenBy(o => o.Row.Id)
                .Take(PerStore)
                .Select(o => new CatalogEquivalentItem(CatalogEndpoints.ToItem(o.Row, o.Price, today), o.Verdict.Basis, o.Verdict.Score, o.Compare))
                .ToList();
            return new StoreEquivalents(store.Slug(), store.DisplayName(), items);
        }).ToList();

        return new CatalogEquivalentsResponse(
            new CatalogEquivalentItem(CatalogEndpoints.ToItem(source, prices.GetValueOrDefault(source.Id), today), null, null, sourceCompare),
            searchable ? null : ReasonNoSizeOrBarcode,
            stores);
    }

    /// <summary>
    /// The unit price of a row. A row is comparable when it has a size, a current price, is in stock and was listed within the last
    /// <see cref="PromotionBuilder.StaleAfterDays"/> days (the Plan's and Promotions' window, here against the day a sweep last listed
    /// it, because a catalog price's own date is only when its value first appeared); a flagged row still shows its figure but ranks
    /// last and has no difference. Only rows in the source's kind of unit are compared with it.
    /// </summary>
    private static CatalogComparison Compare(StoreProduct row, StoreProductPrice? latest, DateOnly today, CatalogComparison? source)
    {
        var flags = new List<string>();
        var size = PackOf(row);
        if (size is null)
        {
            flags.Add(FlagNoSize);
        }

        var price = latest is null ? null : CatalogEndpoints.ToPrice(latest, today);
        if (price?.Best is null)
        {
            flags.Add(FlagNoPrice);
        }
        else
        {
            if (!price.InStock)
            {
                flags.Add(FlagOutOfStock);
            }

            if (today.DayNumber - PriceRecorder.DublinDay(row.LastSeenAt).DayNumber > PromotionBuilder.StaleAfterDays)
            {
                flags.Add(FlagStale);
            }
        }

        decimal? unitPrice = size is { } pack && price?.Best is { } best ? UnitPricing.PerUnit(best, pack) : null;
        var unit = size?.Unit;
        if (source is { Unit: { } sourceUnit } && unit is not null && unit != sourceUnit)
        {
            flags.Add(FlagOtherUnit);
        }

        var comparable = unitPrice is not null && !flags.Contains(FlagStale) && !flags.Contains(FlagOutOfStock);
        decimal? difference = source is { UnitPrice: { } mine } && IsComparable(source) && comparable && !flags.Contains(FlagOtherUnit)
            ? unitPrice - mine
            : null;
        return new CatalogComparison(unit, size?.Label, unitPrice, difference, flags);
    }

    private static bool IsComparable(CatalogComparison c) =>
        c.UnitPrice is not null && !c.Flags.Contains(FlagStale) && !c.Flags.Contains(FlagOutOfStock);

    /// <summary>The row's size in the unit its price is compared per: kilograms, litres or pieces, all packs of a multipack counted.</summary>
    private static PackSize? PackOf(StoreProduct row)
    {
        if (row.SizeAmount is not { } amount || amount <= 0 || row.SizeUnit is not { } unit)
        {
            return null;
        }

        var total = amount * Math.Max(row.SizePacks, 1);
        return unit switch
        {
            StoreProduct.Grams => new PackSize(total / 1000m, UnitKind.Mass),
            StoreProduct.Millilitres => new PackSize(total / 1000m, UnitKind.Volume),
            StoreProduct.Each => new PackSize(total, UnitKind.Piece),
            _ => null,
        };
    }
}
