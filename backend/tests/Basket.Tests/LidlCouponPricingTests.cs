using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Promotions;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>
/// An activated Lidl Plus coupon changing a Lidl price (docs/decisions.md, "Lidl Plus coupons"). The expected numbers are worked by
/// hand: 20% off 4.00 is 3.20, €1 off 4.00 is 3.00.
/// </summary>
public sealed class LidlCouponPricingTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static ActiveCoupon Coupon(string title, string? discount = null, string? description = null, DateOnly? until = null) =>
        new("C-1", title, discount, description, until);

    private sealed record Shelf(Product Product, ILookup<Guid, StoreListing> Listings, IReadOnlyDictionary<Guid, PriceObservation> Latest);

    /// <summary>One product listed at Lidl (the shelf price given, titled as given) and at Aldi (its own price).</summary>
    private static Shelf Build(string lidlTitle, decimal lidlPrice, decimal aldiPrice, string? lidlPromo = null)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Chicken breast fillets" };
        var lidl = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Lidl, Title = lidlTitle };
        var aldi = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Aldi, Title = "Aldi chicken breast fillets 500g" };
        var latest = new Dictionary<Guid, PriceObservation>
        {
            [lidl.Id] = new() { ListingId = lidl.Id, ObservedOn = Today, Price = lidlPrice, PromoText = lidlPromo, InStock = true },
            [aldi.Id] = new() { ListingId = aldi.Id, ObservedOn = Today, Price = aldiPrice, InStock = true },
        };
        return new Shelf(product, new[] { lidl, aldi }.ToLookup(l => l.ProductId), latest);
    }

    private static ProductComparison Compare(Shelf shelf, params ActiveCoupon[] coupons) =>
        PromotionBuilder.Compare(shelf.Product, shelf.Listings, shelf.Latest, Today, null, coupons);

    private static PromoOffer Lidl(ProductComparison comparison) => comparison.Offers.Single(o => o.Store == "lidl");

    [Fact]
    public void A_percentage_coupon_that_names_the_product_takes_that_much_off_the_shelf_price_and_can_make_Lidl_win()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        var without = Compare(shelf);
        var with = Compare(shelf, Coupon("Test chicken breast fillets", "20% off"));

        Lidl(without).Kind.Should().Be("regular");
        without.Offers.First().Store.Should().Be("aldi", "Aldi is cheaper at the shelf");
        var offer = Lidl(with);
        offer.Kind.Should().Be("coupon");
        offer.IsPromo.Should().BeTrue();
        offer.Deal.Should().Be("Coupon: 20% off");
        offer.EachPrice.Should().Be(3.20m);
        offer.ShelfPrice.Should().Be(4.00m);
        offer.Card.Should().Be("Lidl Plus");
        offer.SavingPercent.Should().Be(20);
        with.Offers.First().Store.Should().Be("lidl", "the coupon price beats Aldi's 3.50");
        with.Offers.First().Rank.Should().Be(1);
    }

    [Fact]
    public void An_amount_coupon_takes_euros_off()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        var offer = Lidl(Compare(shelf, Coupon("Test chicken breast fillets", "€1 off")));

        offer.EachPrice.Should().Be(3.00m);
        offer.Deal.Should().Be("Coupon: €1 off");
    }

    [Fact]
    public void The_wording_can_be_in_the_title_or_the_description()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        Lidl(Compare(shelf, Coupon("Save 25% on test chicken breast fillets"))).EachPrice.Should().Be(3.00m);
        Lidl(Compare(shelf, Coupon("Test chicken breast fillets", null, "Get €0.50 off at the till"))).EachPrice.Should().Be(3.50m);
    }

    [Fact]
    public void A_coupon_that_does_not_name_the_product_is_not_applied()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        Lidl(Compare(shelf, Coupon("Fresh fruit", "20% off"))).Kind.Should().Be("regular");
        Lidl(Compare(shelf, Coupon("Chicken thighs", "20% off"))).Kind.Should().Be("regular", "one shared word is not the product");
    }

    [Fact]
    public void A_listing_with_one_significant_word_is_never_matched_by_name()
    {
        var shelf = Build("Milk 2L", 1.50m, 1.40m);

        Lidl(Compare(shelf, Coupon("Almond milk", "20% off"))).Kind.Should().Be("regular");
    }

    [Theory]
    [InlineData("Spend €50 and save")]
    [InlineData("Buy 2 get 1 free")]
    [InlineData("2 for €6")]
    [InlineData("")]
    public void A_coupon_whose_wording_is_neither_a_percentage_nor_euros_off_is_not_applied(string wording)
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        Lidl(Compare(shelf, Coupon("Test chicken breast fillets", wording))).Kind.Should().Be("regular");
    }

    [Theory]
    [InlineData("Spend €30 and save €5")]
    [InlineData("Buy 2, get 20% off")]
    [InlineData("€1 off when you buy 2")]
    [InlineData("€1 off, minimum spend €10")]
    public void A_coupon_with_a_spend_or_quantity_condition_is_not_applied_even_when_an_amount_can_be_read(string wording)
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        Lidl(Compare(shelf, Coupon("Test chicken breast fillets", wording))).Kind.Should().Be("regular");
    }

    [Fact]
    public void A_coupon_is_only_taken_when_it_beats_the_price_the_reading_already_gives()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m, lidlPromo: "2 for €5");

        var offer = Lidl(Compare(shelf, Coupon("Test chicken breast fillets", "10% off")));

        offer.Kind.Should().Be("multi_buy");
        offer.EachPrice.Should().Be(2.50m);
    }

    [Fact]
    public void A_coupon_never_touches_another_shop_and_a_price_never_goes_to_zero_or_below()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);
        var comparison = Compare(shelf, Coupon("Aldi chicken breast fillets", "50% off"), Coupon("Test chicken breast fillets", "€5 off"));

        comparison.Offers.Single(o => o.Store == "aldi").Kind.Should().Be("regular");
        comparison.Offers.Single(o => o.Store == "aldi").EachPrice.Should().Be(3.50m);
        Lidl(comparison).Kind.Should().Be("regular", "€5 off a €4 pack is not a price");
    }

    [Fact]
    public void The_offer_ends_with_the_coupon_when_that_is_sooner()
    {
        var shelf = Build("Test chicken breast fillets 500g", 4.00m, 3.50m);

        Lidl(Compare(shelf, Coupon("Test chicken breast fillets", "20% off", until: new DateOnly(2026, 11, 22)))).ValidUntil
            .Should().Be(new DateOnly(2026, 11, 22));
    }

    [Theory]
    [InlineData("20% off", 20)]
    [InlineData("Save 15%", 15)]
    public void Percentages_are_read(string text, int percent) => PromoText.ParsePercent(text).Should().Be(percent);

    [Theory]
    [InlineData("€1 off", 1.0)]
    [InlineData("€1.50 OFF", 1.5)]
    [InlineData("1 € off", 1.0)]
    [InlineData("Save €2", 2.0)]
    [InlineData("Save €0,75", 0.75)]
    [InlineData("€1 discount", 1.0)]
    public void Amounts_off_are_read(string text, double euros) => PromoText.ParseAmountOff(text).Should().Be((decimal)euros);

    [Theory]
    [InlineData("20% off")]
    [InlineData("2 for €4")]
    [InlineData("€3.50")]
    [InlineData("Save 20%")]
    [InlineData("")]
    [InlineData(null)]
    public void Wording_that_is_not_an_amount_off_is_left_alone(string? text) => PromoText.ParseAmountOff(text).Should().BeNull();
}
