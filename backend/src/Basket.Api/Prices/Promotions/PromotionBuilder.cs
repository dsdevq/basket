using Basket.Api.Data;

namespace Basket.Api.Prices.Promotions;

/// <summary>
/// One way to buy a product in one shop, normalised. <see cref="UnitPrice"/> is per <see cref="Unit"/> (kg, l or each),
/// null when the pack size is not known. <see cref="Rank"/> is the place among the shops that were comparable (1 is
/// cheapest per unit), null when this one was left out of the ranking; <see cref="Flags"/> says why.
/// </summary>
public sealed record PromoOffer(
    string Store,
    string StoreName,
    string Kind,
    bool IsPromo,
    string Deal,
    string? PromoText,
    string? Card,
    int BuyQuantity,
    decimal PayTotal,
    decimal EachPrice,
    decimal? ShelfPrice,
    decimal? ReferencePrice,
    int? SavingPercent,
    string? PackSize,
    string? Unit,
    decimal? UnitPrice,
    decimal? ReferenceUnitPrice,
    DateOnly ObservedOn,
    DateOnly? ValidUntil,
    int? Rank,
    IReadOnlyList<string> Flags);

/// <summary>A shop that is linked but has nothing usable today. <see cref="Reason"/>: not_read, no_price, upcoming or ended.</summary>
public sealed record PromoGap(string Store, string StoreName, string Reason, DateOnly? Date);

public sealed record PromoProduct(
    Guid Id,
    string Name,
    string? Unit,
    int Ranked,
    IReadOnlyList<PromoOffer> Offers,
    IReadOnlyList<PromoGap> Gaps);

/// <summary>The offers of one product, ranked where they can be (see <see cref="PromotionBuilder.Compare"/>), and the shops with a gap.</summary>
public sealed record ProductComparison(string? Unit, int Ranked, IReadOnlyList<PromoOffer> Offers, IReadOnlyList<PromoGap> Gaps);

public sealed record PromotionsResponse(DateOnly AsOf, int StaleAfterDays, IReadOnlyList<PromoProduct> Products);

/// <summary>
/// Turns the latest reading of every linked shop page into a ranked comparison per product. Pure: it reads nothing but
/// what it is given, so the rule is testable without a database.
/// </summary>
public static class PromotionBuilder
{
    /// <summary>A reading older than this many days is shown but flagged and left out of the ranking: its price may have moved.</summary>
    public const int StaleAfterDays = 3;

    public const string FlagStale = "stale";
    public const string FlagOutOfStock = "out_of_stock";
    public const string FlagNoSize = "no_size";
    public const string FlagSizeFromName = "size_from_name";
    public const string FlagOtherUnit = "other_unit";

    private static readonly string[] UnitOrder = ["kg", "l", "each"];

    public static PromotionsResponse Build(
        IEnumerable<Product> products,
        ILookup<Guid, StoreListing> listings,
        IReadOnlyDictionary<Guid, PriceObservation> latest,
        DateOnly today)
    {
        var result = new List<PromoProduct>();
        foreach (var product in products.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var comparison = Compare(product, listings, latest, today);
            if (comparison.Offers.Any(o => o.IsPromo))
            {
                result.Add(new PromoProduct(product.Id, product.Name, comparison.Unit, comparison.Ranked, comparison.Offers, comparison.Gaps));
            }
        }

        return new PromotionsResponse(today, StaleAfterDays, result);
    }

    /// <summary>
    /// One product across its linked shops: every usable reading as a normalised, ranked offer, and every linked shop with
    /// nothing usable today as a gap. The Promotions view lists only the products that have a promo; the plan reads every
    /// product on the list through the same rule, so the two never disagree about a price.
    /// </summary>
    public static ProductComparison Compare(
        Product product,
        ILookup<Guid, StoreListing> listings,
        IReadOnlyDictionary<Guid, PriceObservation> latest,
        DateOnly today)
    {
        var offers = new List<PromoOffer>();
        var gaps = new List<PromoGap>();
        foreach (var store in Stores.All)
        {
            var listing = listings[product.Id].FirstOrDefault(l => l.Store == store);
            if (listing is null)
            {
                continue;
            }

            if (!latest.TryGetValue(listing.Id, out var observation))
            {
                gaps.Add(new PromoGap(Slug(store), store.DisplayName(), "not_read", null));
                continue;
            }

            var state = PriceStates.Of(observation, today);
            if (state != PriceStates.Current)
            {
                gaps.Add(Gap(store, state, observation));
                continue;
            }

            var offer = Offer(store, product, listing, observation, today);
            if (offer is null)
            {
                gaps.Add(new PromoGap(Slug(store), store.DisplayName(), "no_price", null));
                continue;
            }

            offers.Add(offer);
        }

        var (ranked, unit, count) = Rank(offers);
        return new ProductComparison(unit, count, ranked, gaps);
    }

    private static PromoGap Gap(Store store, string state, PriceObservation observation) => state switch
    {
        PriceStates.Upcoming => new PromoGap(Slug(store), store.DisplayName(), "upcoming", observation.ValidFrom),
        PriceStates.Ended => new PromoGap(Slug(store), store.DisplayName(), "ended", observation.ValidUntil),
        _ => new PromoGap(Slug(store), store.DisplayName(), "no_price", null),
    };

    private static PromoOffer? Offer(Store store, Product product, StoreListing listing, PriceObservation o, DateOnly today)
    {
        var reading = new Reading(o.Price, o.WasPrice, o.LoyaltyPrice, o.LoyaltyScheme, o.PromoText);
        if (OfferCalculator.Evaluate(reading) is not { } deal)
        {
            return null;
        }

        var flags = new List<string>();
        if (today.DayNumber - o.ObservedOn.DayNumber > StaleAfterDays)
        {
            flags.Add(FlagStale);
        }

        if (!o.InStock)
        {
            flags.Add(FlagOutOfStock);
        }

        var size = PackSizes.Read(listing.Title, product.Name);
        string? packLabel = null;
        string? unit = null;
        decimal? unitPrice = null;
        decimal? referenceUnit = null;
        if (size is { } found)
        {
            packLabel = found.Size.Label;
            unit = found.Size.Unit;
            unitPrice = UnitPricing.PerUnit(deal.EachPrice, found.Size);
            referenceUnit = deal.ReferencePrice is { } reference && reference > deal.EachPrice
                ? UnitPricing.PerUnit(reference, found.Size)
                : null;
            if (found.Source == 1)
            {
                flags.Add(FlagSizeFromName);
            }
        }
        else
        {
            flags.Add(FlagNoSize);
        }

        var isPromo = deal.Kind != DealKind.Regular;
        return new PromoOffer(
            Slug(store),
            store.DisplayName(),
            Kind(deal.Kind),
            isPromo,
            deal.Label,
            o.PromoText,
            deal.Card,
            deal.BuyQuantity,
            deal.PayTotal,
            deal.EachPrice,
            deal.ShelfPrice,
            deal.ReferencePrice,
            deal.SavingPercent,
            packLabel,
            unit,
            unitPrice,
            referenceUnit,
            o.ObservedOn,
            o.ValidUntil,
            null,
            flags);
    }

    /// <summary>
    /// Ranks the comparable offers by unit price, cheapest first, equal prices sharing a place. Comparable means fresh, in
    /// stock and with a known pack size; a product whose shops sell it in different kinds of unit (kg here, pieces there)
    /// is ranked in the kind most shops share and the others are flagged. The rest follow, unranked.
    /// </summary>
    private static (List<PromoOffer> Offers, string? Unit, int Count) Rank(List<PromoOffer> offers)
    {
        var comparable = offers.Where(o => o.UnitPrice is not null && !o.Flags.Contains(FlagStale) && !o.Flags.Contains(FlagOutOfStock)).ToList();
        var unit = comparable
            .GroupBy(o => o.Unit!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Array.IndexOf(UnitOrder, g.Key))
            .Select(g => g.Key)
            .FirstOrDefault();
        var ranked = comparable.Where(o => o.Unit == unit).OrderBy(o => o.UnitPrice).ToList();

        var places = new Dictionary<PromoOffer, int>();
        for (var i = 0; i < ranked.Count; i++)
        {
            places[ranked[i]] = i > 0 && ranked[i].UnitPrice == ranked[i - 1].UnitPrice ? places[ranked[i - 1]] : i + 1;
        }

        var rest = offers
            .Where(o => !places.ContainsKey(o))
            .Select(o => comparable.Contains(o) ? o with { Flags = [.. o.Flags, FlagOtherUnit] } : o)
            .OrderBy(o => o.Store, StringComparer.Ordinal);
        var ordered = ranked.Select(o => o with { Rank = places[o] }).Concat(rest).ToList();
        return (ordered, unit, ranked.Count);
    }

    private static string Kind(DealKind kind) => kind switch
    {
        DealKind.MultiBuy => "multi_buy",
        DealKind.Loyalty => "loyalty",
        DealKind.Percentage => "percentage",
        _ => "regular",
    };

    private static string Slug(Store store) => store.ToString().ToLowerInvariant();
}
