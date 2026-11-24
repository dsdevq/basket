using Basket.Api.Data;
using Basket.Api.Lists;
using Basket.Api.Matching;
using Basket.Api.Prices.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Promotions;

/// <summary>One shop's offer on a catalog row that is the usual product. <see cref="Basis"/> is <c>linked</c> (the product's own page) or the matcher's basis.</summary>
public sealed record UsualOffer(Guid CatalogProductId, string Title, string? ImageUrl, string Url, string Basis, PromoOffer Offer);

public sealed record UsualProduct(Guid Id, string Name, int BoughtIn, IReadOnlyList<UsualOffer> Offers);

public sealed record UsualOffersResponse(DateOnly AsOf, int StaleAfterDays, IReadOnlyList<UsualProduct> Products);

/// <summary>
/// The offers on what the household usually buys, read from the whole catalog and not only from the pages linked to a product.
/// A usual product (<see cref="HabitEndpoints"/>: bought in at least 2 of the latest 10 closed trips) is looked up in every shop's
/// catalog by the same narrowing and matcher the server's candidate search runs (<see cref="CatalogRows"/>, <see cref="CandidateMatcher"/>,
/// the product's other names included), so a Lidl offer on a product with no linked Lidl page still shows. A row counts when its
/// price today is a promotion by the Promotions rule (<see cref="PromotionBuilder.CatalogOffer"/>), it is in stock and a sweep listed
/// it within <see cref="PromotionBuilder.StaleAfterDays"/> days. A page a person rejected for the product is never shown.
/// Read only: nothing is proposed, linked or stored, there is no migration, and nothing is pushed to anyone.
/// </summary>
public static class UsualOffers
{
    /// <summary>Most offers shown per product per shop: a product with a dozen near-identical rows must not fill the screen.</summary>
    public const int PerStore = 2;

    public static async Task<UsualOffersResponse> BuildAsync(AppDbContext db, DateOnly today, CancellationToken ct)
    {
        var usual = (await HabitEndpoints.LoadAsync(db, ct)).Where(h => h.Usual).ToList();
        var ids = usual.Select(h => h.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var listings = (await db.StoreListings.AsNoTracking().Where(l => ids.Contains(l.ProductId)).ToListAsync(ct)).ToLookup(l => l.ProductId);
        var aliases = await ProductAliases.TextsAsync(db, ids, ct);
        var rejected = (await db.MatchCandidates.AsNoTracking().Where(c => ids.Contains(c.ProductId) && c.Status == CandidateStatus.Rejected)
                .Select(c => new { c.ProductId, c.Url }).ToListAsync(ct))
            .ToLookup(c => c.ProductId, c => c.Url);
        var coupons = await CouponPricing.ActiveAsync(db, today, ct);

        var linkedIds = listings.SelectMany(g => g).Where(l => l.ConfirmedAt != null && l.StoreProductId != null).Select(l => l.StoreProductId!.Value).ToList();
        var linked = await db.StoreProducts.AsNoTracking().Where(p => linkedIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        var result = new List<UsualProduct>();
        foreach (var habit in usual)
        {
            if (!products.TryGetValue(habit.ProductId, out var product))
            {
                continue;
            }

            var own = listings[product.Id].ToList();
            var facts = ServerCandidates.FactsOf(product, own, linked);
            var names = ProductAliases.FactsWithAliases(facts, aliases[product.Id]);
            var refused = rejected[product.Id].ToHashSet(StringComparer.Ordinal);

            var rows = new Dictionary<Guid, (StoreProduct Row, string Basis, int Score)>();
            foreach (var listing in own.Where(l => l.StoreProductId != null))
            {
                // The product's own page, whoever linked it: it is what the household reads prices from.
                if (await db.StoreProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == listing.StoreProductId && p.DiscontinuedAt == null, ct) is { } row)
                {
                    rows[row.Id] = (row, "linked", 100);
                }
            }

            foreach (var store in Stores.All)
            {
                foreach (var name in names)
                {
                    foreach (var row in await CatalogRows.NarrowAsync(db, store, name, ct))
                    {
                        if (rows.ContainsKey(row.Id) || refused.Contains(row.Url))
                        {
                            continue;
                        }

                        if (CandidateMatcher.EvaluateAny(names, new CandidateFacts(CatalogRows.TitleOf(row), row.Barcode, row.Category, CatalogRows.SizeOf(row))) is { } verdict)
                        {
                            rows[row.Id] = (row, verdict.Basis, verdict.Score);
                        }
                    }
                }
            }

            var prices = await CatalogEndpoints.LatestPricesAsync(db, rows.Keys.ToList(), ct);
            var offers = new List<UsualOffer>();
            foreach (var (id, (row, basis, score)) in rows)
            {
                var seen = PriceRecorder.DublinDay(row.LastSeenAt);
                if (!prices.TryGetValue(id, out var price) || today.DayNumber - seen.DayNumber > PromotionBuilder.StaleAfterDays)
                {
                    continue;
                }

                var title = CatalogRows.TitleOf(row);
                var offer = PromotionBuilder.CatalogOffer(row.Store, row.SizeText is { Length: > 0 } size && !title.Contains(size, StringComparison.OrdinalIgnoreCase) ? $"{title} {size}" : title, product.Name, price, seen, today, coupons);
                if (offer is { IsPromo: true } && !offer.Flags.Contains(PromotionBuilder.FlagOutOfStock))
                {
                    offers.Add(new UsualOffer(row.Id, title, row.ImageUrl, row.Url, basis, offer));
                }
            }

            var shown = offers
                .GroupBy(o => o.Offer.Store)
                .SelectMany(g => g
                    .OrderBy(o => o.Basis == "linked" ? 0 : 1)
                    .ThenByDescending(o => rows[o.CatalogProductId].Score)
                    .ThenByDescending(o => o.Offer.SavingPercent ?? 0)
                    .ThenBy(o => o.Offer.EachPrice)
                    .ThenBy(o => o.Title, StringComparer.Ordinal)
                    .Take(PerStore))
                .OrderBy(o => o.Offer.Store, StringComparer.Ordinal)
                .ThenBy(o => o.Offer.EachPrice)
                .ToList();
            if (shown.Count > 0)
            {
                result.Add(new UsualProduct(product.Id, product.Name, habit.BoughtIn, shown));
            }
        }

        return new UsualOffersResponse(today, PromotionBuilder.StaleAfterDays, result);
    }
}
