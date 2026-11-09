using Basket.Api.Prices.Promotions;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>
/// The normalisation rule on its own. The expected numbers are worked by hand from the rule in docs/decisions.md
/// ("Promotions"), not by calling the code under test.
/// </summary>
public sealed class PromotionRuleTests
{
    private static Deal Deal(decimal? price, decimal? was = null, decimal? loyalty = null, string? scheme = null, string? promo = null) =>
        OfferCalculator.Evaluate(new Reading(price, was, loyalty, scheme, promo))!;

    // ---- multi-buy ------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_for_four_euros_is_two_euros_each_whatever_the_single_price()
    {
        var deal = Deal(2.19m, promo: "Mix 'n' Match 2 for €4");

        deal.Kind.Should().Be(DealKind.MultiBuy);
        deal.Label.Should().Be("2 for €4");
        deal.BuyQuantity.Should().Be(2);
        deal.PayTotal.Should().Be(4m);
        deal.EachPrice.Should().Be(2m);
        deal.Card.Should().BeNull("no loyalty price sits beside the text, so it is for everyone");
        deal.ReferencePrice.Should().Be(2.19m);
        deal.SavingPercent.Should().Be(9, "(2.19 - 2.00) / 2.19 = 8.7%");
    }

    [Fact]
    public void A_multi_buy_beside_a_card_price_is_the_cards_as_on_lidls_tiles()
    {
        // Lidl's tile: the Lidl Plus price equals the shelf price and the "2 for €4" text is the Lidl Plus block's.
        var deal = Deal(2.19m, loyalty: 2.19m, scheme: "Lidl Plus", promo: "Mix 'n' Match 2 for €4");

        deal.Kind.Should().Be(DealKind.MultiBuy);
        deal.EachPrice.Should().Be(2m);
        deal.Card.Should().Be("Lidl Plus");
    }

    [Theory]
    [InlineData(1.50, "3 for 2", 3, 3.00, 1.00, 33)] // pay for two of three: 2 x 1.50 = 3.00, 1.00 each, saves a third
    [InlineData(2.00, "2 for 3", 2, 3.00, 1.50, 25)] // no € sign and 3 > 2: €3 for two
    [InlineData(2.00, "2 for €3.50", 2, 3.50, 1.75, 13)] // 12.5 rounds away from zero
    [InlineData(3.00, "Buy 1 get 1 free", 2, 3.00, 1.50, 50)]
    [InlineData(3.00, "BOGOF", 2, 3.00, 1.50, 50)]
    [InlineData(1.20, "Buy two get one free", 3, 2.40, 0.80, 33)]
    public void Multi_buy_wordings_all_become_one_price_per_pack(double price, string text, int quantity, double total, double each, int saving)
    {
        var deal = Deal((decimal)price, promo: text);

        deal.Kind.Should().Be(DealKind.MultiBuy);
        deal.BuyQuantity.Should().Be(quantity);
        deal.PayTotal.Should().Be((decimal)total);
        deal.EachPrice.Should().Be((decimal)each);
        deal.SavingPercent.Should().Be(saving);
    }

    [Fact]
    public void A_multi_buy_dearer_than_buying_one_is_not_the_deal()
    {
        var deal = Deal(1.00m, promo: "2 for €3");

        deal.Kind.Should().Be(DealKind.Regular);
        deal.BuyQuantity.Should().Be(1);
        deal.EachPrice.Should().Be(1.00m);
    }

    [Theory]
    [InlineData("2 for 2")]
    [InlineData("1 for €1")]
    [InlineData("Great value")]
    [InlineData("Ends Sunday")]
    [InlineData("")]
    [InlineData(null)]
    public void Wording_that_says_nothing_about_a_quantity_price_is_left_alone(string? text) =>
        PromoText.ParseMultiBuy(text).Should().BeNull();

    // ---- percentage -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_was_price_makes_a_percentage_offer_and_the_shown_price_is_the_price()
    {
        var deal = Deal(1.60m, was: 2.00m);

        deal.Kind.Should().Be(DealKind.Percentage);
        deal.Label.Should().Be("20% off");
        deal.EachPrice.Should().Be(1.60m);
        deal.ReferencePrice.Should().Be(2.00m);
        deal.SavingPercent.Should().Be(20);
    }

    [Fact]
    public void A_percentage_in_the_text_without_a_was_price_is_shown_but_never_applied_again()
    {
        var deal = Deal(2.40m, promo: "20% off");

        deal.Kind.Should().Be(DealKind.Percentage);
        deal.Label.Should().Be("20% off");
        deal.EachPrice.Should().Be(2.40m, "the shop's price is what is charged; the text is not subtracted a second time");
        deal.SavingPercent.Should().BeNull("with no old price the saving is unknown, not guessed");
    }

    [Theory]
    [InlineData("20% off", 20)]
    [InlineData("Save 15%", 15)]
    [InlineData("12.5% discount", 12.5)]
    public void Percentages_are_read_from_the_promo_text(string text, double expected) =>
        PromoText.ParsePercent(text).Should().Be((decimal)expected);

    // ---- loyalty price --------------------------------------------------------------------------------------------

    [Fact]
    public void A_card_price_below_the_shelf_price_is_the_deal_and_needs_the_card()
    {
        var deal = Deal(1.29m, was: 1.49m, loyalty: 0.99m, scheme: "Lidl Plus", promo: "Lidl Plus deal");

        deal.Kind.Should().Be(DealKind.Loyalty);
        deal.Label.Should().Be("Lidl Plus price");
        deal.Card.Should().Be("Lidl Plus");
        deal.EachPrice.Should().Be(0.99m);
        deal.ShelfPrice.Should().Be(1.29m);
        deal.ReferencePrice.Should().Be(1.49m, "measured against the was-price when the shop gives one");
        deal.SavingPercent.Should().Be(34, "(1.49 - 0.99) / 1.49 = 33.6%");
    }

    [Fact]
    public void A_card_only_price_with_no_shelf_price_is_still_a_deal_with_no_saving()
    {
        var deal = Deal(null, loyalty: 0.99m, scheme: "Clubcard");

        deal.Kind.Should().Be(DealKind.Loyalty);
        deal.Card.Should().Be("Clubcard");
        deal.ShelfPrice.Should().BeNull();
        deal.SavingPercent.Should().BeNull();
    }

    [Fact]
    public void A_card_price_that_is_not_lower_is_not_a_deal()
    {
        var deal = Deal(2.00m, loyalty: 2.00m, scheme: "Clubcard");

        deal.Kind.Should().Be(DealKind.Regular);
        deal.Card.Should().BeNull();
    }

    [Fact]
    public void A_card_multi_buy_beats_the_card_price_when_it_is_cheaper_each()
    {
        // Clubcard price 1.80, and "2 for €3" on the Clubcard block: 1.50 each beats 1.80.
        var deal = Deal(2.00m, loyalty: 1.80m, scheme: "Clubcard", promo: "Clubcard Price 2 for €3");

        deal.Kind.Should().Be(DealKind.MultiBuy);
        deal.EachPrice.Should().Be(1.50m);
        deal.Card.Should().Be("Clubcard");
    }

    [Fact]
    public void A_card_three_for_two_takes_two_of_the_card_price()
    {
        var deal = Deal(2.00m, loyalty: 1.50m, scheme: "Lidl Plus", promo: "3 for 2");

        deal.EachPrice.Should().Be(1.00m, "2 x 1.50 = 3.00 for three");
        deal.Card.Should().Be("Lidl Plus");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, 2.00, null)] // a was-price alone is not a price
    [InlineData(0.0, null, 0.0)] // a zero is no price
    public void A_reading_with_no_usable_price_has_no_deal(double? price, double? was, double? loyalty) =>
        OfferCalculator.Evaluate(new Reading((decimal?)price, (decimal?)was, (decimal?)loyalty, "Clubcard", "Special")).Should().BeNull();

    [Fact]
    public void The_shelf_price_alone_is_regular()
    {
        var deal = Deal(2.50m);

        deal.Kind.Should().Be(DealKind.Regular);
        deal.Label.Should().Be("Shelf price");
        deal.SavingPercent.Should().BeNull();
    }

    // ---- pack sizes and unit prices -------------------------------------------------------------------------------

    [Theory]
    [InlineData("Clonbawn Protein Milk 1L", 1, "l", "1 l")]
    [InlineData("Chicken breast fillets 500g", 0.5, "kg", "500 g")]
    [InlineData("Basmati rice 1.5kg", 1.5, "kg", "1.5 kg")]
    [InlineData("Olive oil 1,5L", 1.5, "l", "1.5 l")]
    [InlineData("Red wine 75cl", 0.75, "l", "750 ml")]
    [InlineData("Cola 6 x 330ml", 1.98, "l", "1.98 l")]
    [InlineData("Yoghurt 4x125g", 0.5, "kg", "500 g")]
    [InlineData("Butter 250G", 0.25, "kg", "250 g")]
    [InlineData("Free range eggs pack of 6", 6, "each", "6 pieces")]
    [InlineData("Toilet roll 9 pack", 9, "each", "9 pieces")]
    [InlineData("Aubergine each", 1, "each", "1 piece")]
    public void Pack_sizes_are_read_from_the_name_in_kilograms_litres_or_pieces(string name, double quantity, string unit, string label)
    {
        var size = PackSizes.Parse(name);

        size.Should().NotBeNull();
        size!.Value.Quantity.Should().Be((decimal)quantity);
        size.Value.Unit.Should().Be(unit);
        size.Value.Label.Should().Be(label);
    }

    [Theory]
    [InlineData("VITASIA 10 Chicken Breast Skewers")]
    [InlineData("Cranberry Sauce")]
    [InlineData("Heinz Beans 415g x 4")] // a measure beside a separate pack count: 0.415 kg or 1.66 kg?
    [InlineData("Coca-Cola 330ml 6 pack")]
    [InlineData("Coca-Cola 330ml pack of 6")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_with_no_size_has_none_it_is_never_guessed(string? name) =>
        PackSizes.Parse(name).Should().BeNull();

    [Fact]
    public void The_shops_name_is_read_before_the_household_name()
    {
        PackSizes.Read("Aldi milk 1L", "Whole milk 2L")!.Value.Should().Be((new PackSize(1m, UnitKind.Volume), 0));
        PackSizes.Read("Aldi milk", "Whole milk 2L")!.Value.Should().Be((new PackSize(2m, UnitKind.Volume), 1));
        PackSizes.Read("Aldi milk", "Whole milk").Should().BeNull();
    }

    [Theory]
    [InlineData("Coke 330ml 6 pack", "Coke 330ml")]
    [InlineData("Heinz Beans 415g x 4", "Heinz Beans 415g")]
    public void An_ambiguous_shop_name_is_not_replaced_by_the_household_name(string shopName, string householdName) =>
        PackSizes.Read(shopName, householdName).Should().BeNull();

    [Theory]
    [InlineData(2.00, 0.5, 4.00)] // €2 for 500 g
    [InlineData(1.99, 1.98, 1.01)] // 1.99 / 1.98 = 1.0050505, to the cent
    [InlineData(4.00, 0.65, 6.15)] // 6.1538
    [InlineData(3.00, 6, 0.50)] // 3.00 for six pieces
    public void The_unit_price_is_the_pack_price_over_the_pack_size_to_the_cent(double pack, double quantity, double expected) =>
        UnitPricing.PerUnit((decimal)pack, new PackSize((decimal)quantity, UnitKind.Mass)).Should().Be((decimal)expected);
}
