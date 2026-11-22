using System.Globalization;
using System.Text.RegularExpressions;
using Basket.Api.Data;
using Basket.Api.LidlPlus;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Promotions;

/// <summary>A Lidl Plus coupon that is activated and in date today: the only kind that changes a price.</summary>
public sealed record ActiveCoupon(string Id, string Title, string? Discount, string? Description, DateOnly? ValidUntil)
{
    /// <summary>What Lidl says the coupon gives, from the wording and the title together.</summary>
    internal string Wording => string.Join(' ', new[] { Discount, Title, Description }.Where(t => !string.IsNullOrWhiteSpace(t)));
}

/// <summary>
/// Lets an activated Lidl Plus coupon change a Lidl price (written out in docs/decisions.md, "Lidl Plus coupons"). A coupon is
/// applied only when it can be read exactly and named exactly: Lidl's wording is a percentage or euros off, and every significant
/// word of the Lidl listing's title is in the coupon's text. Anything vaguer ("20% off fruit and veg", a spend-and-save) is left alone:
/// a coupon basket cannot tie to the product is never guessed at. It comes off the shelf price of one pack, and counts only when it
/// beats the best price the reading already gives.
/// </summary>
public static partial class CouponPricing
{
    private const int Percent = 100;
    private const int MinWords = 2;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "off", "per", "from", "any", "all", "pack", "lidl", "plus", "coupon", "save", "pcs",
    };

    /// <summary>The coupons stored for the household that are activated and in date on <paramref name="today"/>.</summary>
    public static async Task<IReadOnlyList<ActiveCoupon>> ActiveAsync(AppDbContext db, DateOnly today, CancellationToken ct) =>
        LidlCouponService.ActiveOn(await db.LidlCoupons.AsNoTracking().Where(c => c.Activated).ToListAsync(ct), today)
            .Select(c => new ActiveCoupon(c.Id, c.Title, c.Discount, c.Description, c.ValidUntil))
            .ToList();

    /// <summary>
    /// The deal with the best matching coupon applied, and the day that coupon ends; the deal and null when no coupon applies or none
    /// beats it.
    /// </summary>
    public static (Deal Deal, DateOnly? Until) Apply(Deal deal, Store store, string? listingTitle, IReadOnlyList<ActiveCoupon>? coupons)
    {
        if (coupons is not { Count: > 0 } || store != Store.Lidl || deal.ShelfPrice is not { } shelf)
        {
            return (deal, null);
        }

        var best = deal;
        DateOnly? until = null;
        foreach (var coupon in coupons.Where(c => Names(c, listingTitle)))
        {
            if (Price(shelf, coupon.Wording) is not { } each || each <= 0 || each >= best.EachPrice)
            {
                continue;
            }

            var saving = (int)Math.Round((shelf - each) / shelf * Percent, MidpointRounding.AwayFromZero);
            best = new Deal(DealKind.Coupon, $"Coupon: {Wording(coupon)}", 1, each, each, shelf, shelf, saving > 0 ? saving : null, "Lidl Plus");
            until = coupon.ValidUntil;
        }

        return (best, until);
    }

    /// <summary>The price of one pack with the coupon's wording taken off the shelf price, to the cent; null when the wording is neither a percentage nor an amount.</summary>
    internal static decimal? Price(decimal shelf, string wording)
    {
        if (PromoText.ParsePercent(wording) is { } percent)
        {
            return Math.Round(shelf * (Percent - percent) / Percent, 2, MidpointRounding.AwayFromZero);
        }

        return PromoText.ParseAmountOff(wording) is { } amount ? shelf - amount : null;
    }

    /// <summary>True when the coupon's text holds every significant word of the listing's title (and the title has at least two).</summary>
    internal static bool Names(ActiveCoupon coupon, string? listingTitle)
    {
        var wanted = Words(listingTitle ?? string.Empty);
        if (wanted.Count < MinWords)
        {
            return false;
        }

        var offered = Words(coupon.Wording);
        return wanted.All(offered.Contains);
    }

    private static string Wording(ActiveCoupon coupon)
    {
        var text = coupon.Wording;
        if (PromoText.ParsePercent(text) is { } percent)
        {
            return $"{percent.ToString("0.##", CultureInfo.InvariantCulture)}% off";
        }

        return PromoText.ParseAmountOff(text) is { } amount ? $"{OfferCalculator.Euro(amount)} off" : "off";
    }

    /// <summary>The lower-case words of a text that say something: no sizes or other numbers, no short words, no filler.</summary>
    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !Filler.Contains(w) && !w.Any(char.IsDigit))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
