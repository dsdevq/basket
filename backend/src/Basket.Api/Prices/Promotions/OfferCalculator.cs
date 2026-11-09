using System.Globalization;

namespace Basket.Api.Prices.Promotions;

public enum DealKind
{
    /// <summary>The shelf price, no offer. Kept so a promo can be compared with the ordinary price in the other shops.</summary>
    Regular,

    /// <summary>A price cut: the was-price is higher, or the shop says "20% off".</summary>
    Percentage,

    /// <summary>The card price (Lidl Plus, Clubcard). Needs the card.</summary>
    Loyalty,

    /// <summary>Buy several: "2 for €4", "3 for 2", "buy 1 get 1 free".</summary>
    MultiBuy,
}

/// <summary>What one shop's reading says about a product's price, before any size is known.</summary>
public sealed record Reading(decimal? Price, decimal? WasPrice, decimal? LoyaltyPrice, string? LoyaltyScheme, string? PromoText);

/// <summary>
/// The cheapest way to buy a product in one shop, in one number: <see cref="EachPrice"/>, what one pack costs when you
/// meet the deal's terms (<see cref="BuyQuantity"/> packs, the <see cref="Card"/> if any).
/// </summary>
public sealed record Deal(
    DealKind Kind,
    string Label,
    int BuyQuantity,
    decimal PayTotal,
    decimal EachPrice,
    decimal? ShelfPrice,
    decimal? ReferencePrice,
    int? SavingPercent,
    string? Card);

/// <summary>
/// The normalisation rule (written out in docs/decisions.md, "Promotions"): every way a shop offers a product is turned
/// into the price of one pack, and the cheapest one a customer can actually get is the deal. Dividing by the pack size
/// then gives the unit price (<see cref="UnitPricing"/>), the same figure the shelf labels print.
/// </summary>
public static class OfferCalculator
{
    private const string CardFallback = "loyalty card";
    private const int Percent = 100;

    /// <summary>The best deal in a reading, or null when the reading has no price at all.</summary>
    public static Deal? Evaluate(Reading reading)
    {
        var shelf = reading.Price is > 0 ? reading.Price : null;
        var was = reading.WasPrice is > 0 ? reading.WasPrice : null;
        var card = reading.LoyaltyPrice is > 0 ? reading.LoyaltyPrice : null;
        var scheme = string.IsNullOrWhiteSpace(reading.LoyaltyScheme) ? null : reading.LoyaltyScheme.Trim();
        var cardName = scheme ?? CardFallback;

        var candidates = new List<Candidate>();
        if (shelf is { } plain)
        {
            candidates.Add(new Candidate(1, plain, null, null));
        }

        if (card is { } member && (shelf is null || member < shelf))
        {
            candidates.Add(new Candidate(1, member, cardName, null));
        }

        // A card price and a card promo sit together on the shop's page (Lidl's promo text is the Lidl Plus block's), so a
        // multi-buy beside a card price is the card's. Without a card price it is for everyone.
        var cardPromo = card is not null && scheme is not null;
        var multi = PromoText.ParseMultiBuy(reading.PromoText);
        if (multi is not null)
        {
            var basis = cardPromo ? card ?? shelf : shelf ?? card;
            var total = multi.Total ?? (basis is { } b ? b * multi.PayFor : null);
            if (total is { } together)
            {
                candidates.Add(new Candidate(multi.Quantity, together, cardPromo ? cardName : null, multi));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates
            .OrderBy(c => c.PayTotal / c.Quantity)
            .ThenBy(c => c.Card is not null)
            .ThenBy(c => c.Quantity)
            .First();
        var each = best.PayTotal / best.Quantity;

        var claimed = PromoText.ParsePercent(reading.PromoText);
        var cutFromWas = was is not null && shelf is not null && was > shelf;
        var kind = best.Multi is not null ? DealKind.MultiBuy
            : best.Card is not null ? DealKind.Loyalty
            : cutFromWas || claimed is not null ? DealKind.Percentage
            : DealKind.Regular;

        // What it is measured against: the was-price when the shop gives one, else the ordinary shelf price.
        var reference = was is { } old && old > each ? old : shelf ?? each;
        int? saving = reference > each ? (int)Math.Round((reference - each) / reference * Percent, MidpointRounding.AwayFromZero) : null;
        if (saving == 0)
        {
            saving = null;
        }

        return new Deal(kind, Label(kind, best, saving, claimed, cardName), best.Quantity, best.PayTotal, each, shelf, reference, saving, best.Card);
    }

    private static string Label(DealKind kind, Candidate best, int? saving, decimal? claimed, string cardName) => kind switch
    {
        DealKind.MultiBuy when best.Multi!.PayFor is { } pay => $"{best.Multi.Quantity} for {pay}",
        DealKind.MultiBuy => $"{best.Multi!.Quantity} for {Euro(best.PayTotal)}",
        DealKind.Loyalty => $"{cardName} price",
        DealKind.Percentage when saving is { } cut => $"{cut}% off",
        DealKind.Percentage when claimed is { } said => $"{said.ToString("0.##", CultureInfo.InvariantCulture)}% off",
        _ => "Shelf price",
    };

    /// <summary>€4, €4.50: whole euros lose the cents.</summary>
    public static string Euro(decimal value) =>
        value == decimal.Truncate(value)
            ? $"€{value.ToString("0", CultureInfo.InvariantCulture)}"
            : $"€{value.ToString("0.00", CultureInfo.InvariantCulture)}";

    private sealed record Candidate(int Quantity, decimal PayTotal, string? Card, MultiBuy? Multi);
}

/// <summary>Price per kilogram, litre or piece: the number two packs of different sizes can be compared on.</summary>
public static class UnitPricing
{
    /// <summary>The unit price, to the cent (what a label prints, and what ranks).</summary>
    public static decimal PerUnit(decimal packPrice, PackSize size) =>
        Math.Round(packPrice / size.Quantity, 2, MidpointRounding.AwayFromZero);
}
