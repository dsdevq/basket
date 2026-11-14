using Basket.Api.Matching;
using FluentAssertions;
using Xunit;

namespace Basket.Tests;

public class GtinTests
{
    [Theory]
    [InlineData("4006381333931", "04006381333931")]
    [InlineData("036000291452", "00036000291452")]
    [InlineData("0036000291452", "00036000291452")]
    [InlineData("96385074", "00000096385074")]
    [InlineData(" 4006 3813-33931 ", "04006381333931")]
    [InlineData("04006381333931", "04006381333931")]
    public void A_valid_barcode_is_kept_as_one_14_digit_form(string raw, string expected)
    {
        Gtin.TryNormalize(raw, out var gtin).Should().BeTrue();
        gtin.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4006381333932")] // wrong check digit
    [InlineData("400638133393")] // 12 digits, wrong check
    [InlineData("12345")] // wrong length
    [InlineData("40063813339A1")] // not digits
    public void Anything_else_is_refused(string? raw) => Gtin.TryNormalize(raw, out _).Should().BeFalse();

    [Fact]
    public void Upc_a_and_ean_13_of_the_same_product_compare_equal()
    {
        Gtin.TryNormalize("036000291452", out var upc);
        Gtin.TryNormalize("0036000291452", out var ean);
        upc.Should().Be(ean);
    }
}

public class ProductSizeTests
{
    [Theory]
    [InlineData("Whole milk 2L", SizeUnit.Volume, 2000, 1)]
    [InlineData("Whole milk 2 litres", SizeUnit.Volume, 2000, 1)]
    [InlineData("Cola 1500 ml", SizeUnit.Volume, 1500, 1)]
    [InlineData("Wine 75cl", SizeUnit.Volume, 750, 1)]
    [InlineData("Flour 1.5kg", SizeUnit.Mass, 1500, 1)]
    [InlineData("Flour 1,5 kg", SizeUnit.Mass, 1500, 1)]
    [InlineData("Butter 250g", SizeUnit.Mass, 250, 1)]
    [InlineData("Cola 6 x 330ml", SizeUnit.Volume, 330, 6)]
    [InlineData("Yoghurt 4x125g", SizeUnit.Mass, 125, 4)]
    [InlineData("Tea bags 80 pack", SizeUnit.Count, 80, 1)]
    [InlineData("Eggs pack of 12", SizeUnit.Count, 12, 1)]
    public void The_size_a_name_states_is_reduced_to_a_base_unit(string text, SizeUnit unit, int amount, int packs) =>
        ProductSize.Parse(text).Should().Be(new ProductSize(unit, amount, packs));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Brown bread")]
    [InlineData("Lidl Deluxe lager")] // "l" inside words is not a litre
    public void A_name_without_a_size_has_none(string? text) => ProductSize.Parse(text).Should().BeNull();

    [Fact]
    public void Different_spellings_of_one_size_are_equal() =>
        ProductSize.Parse("Milk 1.5L").Should().Be(ProductSize.Parse("Fresh milk 1500 ml"));

    [Fact]
    public void A_multipack_is_not_one_big_pack() =>
        ProductSize.Parse("Cola 6 x 330ml").Should().NotBe(ProductSize.Parse("Cola 1980ml"));
}

public class CandidateMatcherTests
{
    private const string Barcode = "4006381333931";

    private static MatchVerdict? Verdict(string product, string? title, string? productCode = null, string? candidateCode = null) =>
        CandidateMatcher.Evaluate(new ProductFacts(product, productCode), new CandidateFacts(title, candidateCode));

    [Fact]
    public void The_same_barcode_is_a_match_whatever_the_names_say() =>
        Verdict("Whole milk 2L", "Totally different words", Barcode, "04006381333931").Should().Be(new MatchVerdict(MatchBasis.Barcode, 100));

    [Fact]
    public void Different_barcodes_are_never_a_match_even_with_identical_names() =>
        Verdict("Whole milk 2L", "Whole milk 2L", Barcode, "036000291452").Should().BeNull();

    [Fact]
    public void A_barcode_on_one_side_only_falls_back_to_the_names() =>
        Verdict("Whole milk 2L", "Whole milk 2L", Barcode, null).Should().Be(new MatchVerdict(MatchBasis.NameAndSize, 90));

    [Fact]
    public void An_invalid_barcode_counts_as_no_barcode() =>
        Verdict("Whole milk 2L", "Whole milk 2L", Barcode, "4006381333932")!.Basis.Should().Be(MatchBasis.NameAndSize);

    [Fact]
    public void Equal_names_and_equal_sizes_match_with_the_size_written_another_way() =>
        Verdict("Whole milk 2L", "Whole Milk 2 litres").Should().Be(new MatchVerdict(MatchBasis.NameAndSize, 90));

    [Fact]
    public void Different_sizes_never_match_however_alike_the_names() =>
        Verdict("Whole milk 2L", "Whole milk 1L").Should().BeNull();

    [Fact]
    public void A_multipack_does_not_match_a_single_big_pack() =>
        Verdict("Cola 6 x 330ml", "Cola 1980ml").Should().BeNull();

    [Fact]
    public void Names_that_mostly_agree_match_but_rank_below_an_exact_one()
    {
        var close = Verdict("Whole milk 2L", "Fresh whole milk 2 litres")!;
        close.Basis.Should().Be(MatchBasis.NameAndSize);
        close.Score.Should().BeLessThan(90).And.BeGreaterThan(50);
    }

    [Fact]
    public void Different_products_of_the_same_size_do_not_match() =>
        Verdict("Brown bread 800g", "White bread 800g").Should().BeNull();

    [Fact]
    public void A_missing_size_needs_a_stricter_name_match_and_ranks_lower()
    {
        var verdict = Verdict("Whole milk", "Whole milk 2L")!;
        verdict.Basis.Should().Be(MatchBasis.NameOnly);
        verdict.Score.Should().BeLessThan(50);
        Verdict("Whole milk", "Fresh whole milk 2L").Should().BeNull();
    }

    [Fact]
    public void Accents_and_plurals_do_not_hide_a_match() =>
        Verdict("Creme fraiche 300g", "Crème Fraîche 300 g")!.Basis.Should().Be(MatchBasis.NameAndSize);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_result_with_no_title_and_no_barcode_never_matches(string? title) => Verdict("Whole milk 2L", title).Should().BeNull();

    [Fact]
    public void The_verdict_is_the_same_every_time()
    {
        var first = Verdict("Irish butter 454g", "Kerrygold Irish Butter 454g");
        for (var i = 0; i < 5; i++)
        {
            Verdict("Irish butter 454g", "Kerrygold Irish Butter 454g").Should().Be(first);
        }
    }
}

/// <summary>
/// The brand-agnostic comparison and the category second opinion. Own-label products are the case: each shop sells its own
/// whole milk, and the same kind of product under two shops' labels is what a shopper swaps. Synthetic names throughout.
/// </summary>
public class GenericMatcherTests
{
    private static MatchVerdict? Verdict(string product, string title, string? productCategory = null, string? category = null) =>
        CandidateMatcher.Evaluate(new ProductFacts(product, null, productCategory), new CandidateFacts(title, null, category));

    [Fact]
    public void The_same_kind_of_product_under_two_shops_labels_matches_below_a_name_match()
    {
        var verdict = Verdict("Tesco whole milk 2L", "Milbona whole milk 2 litres")!;

        verdict.Basis.Should().Be(MatchBasis.Generic);
        verdict.Score.Should().Be(45);
        Verdict("Whole milk 2L", "Fresh whole milk 2 litres")!.Score.Should().BeGreaterThan(verdict.Score, "a name and size match outranks a look-alike");
    }

    [Theory]
    [InlineData("Dunnes Stores Simply Better mature cheddar 200g", "Specially Selected mature cheddar 200g")]
    [InlineData("Everyday Essentials plain flour 1kg", "Tesco plain flour 1000 g")]
    [InlineData("Aldi Ferndale butter 250g", "Lidl Milbona butter 250g")]
    public void Own_label_phrases_are_left_out_whole(string product, string title) =>
        Verdict(product, title)!.Basis.Should().Be(MatchBasis.Generic);

    [Fact]
    public void A_manufacturers_brand_is_never_left_out()
    {
        Verdict("Heinz baked beans 400g", "Branston baked beans 400g").Should().BeNull();
        Verdict("Tesco baked beans 400g", "Branston baked beans 400g")!.Basis.Should().Be(MatchBasis.Generic, "the shelf name carries only a shop's label, so any brand of the same beans may be offered");
    }

    [Fact]
    public void Different_generic_names_do_not_match_however_alike_the_labels()
    {
        Verdict("Tesco whole milk 2L", "Milbona skimmed milk 2L").Should().BeNull();
        Verdict("Tesco brown bread 800g", "Milbona white bread 800g").Should().BeNull();
    }

    [Theory]
    [InlineData("Tesco whole milk 2L", "Milbona whole milk 1L")]
    [InlineData("Tesco whole milk", "Milbona whole milk 2L")]
    [InlineData("Tesco whole milk 2L", "Milbona whole milk")]
    public void The_generic_comparison_needs_both_sizes_stated_and_equal(string product, string title) =>
        Verdict(product, title).Should().BeNull();

    // The "6 x 330 ml is not 330 ml" rules: a multipack is not one bottle, whatever the labels and the words say.
    [Theory]
    [InlineData("Tesco cola 6 x 330ml", "Lidl cola 330ml")]
    [InlineData("Tesco cola 330ml", "Lidl cola 6x330 ml")]
    [InlineData("Tesco cola 6 x 330ml", "Lidl cola 1980ml")]
    [InlineData("Tesco cola 6 x 330ml", "Lidl cola 4 x 330ml")]
    [InlineData("Tesco cola 6 x 330ml", "Lidl cola 6 x 500ml")]
    [InlineData("Tesco cola 330ml", "Lidl cola 6 pack")]
    public void A_multipack_is_never_a_single_item_or_another_multipack(string product, string title) =>
        Verdict(product, title).Should().BeNull();

    [Fact]
    public void The_same_multipack_written_two_ways_matches()
    {
        Verdict("Tesco cola 6 x 330ml", "Lidl cola 6x330 ml")!.Basis.Should().Be(MatchBasis.Generic);
        Verdict("Cola 6 x 330ml", "Fresh cola 6 x 33cl")!.Basis.Should().Be(MatchBasis.NameAndSize);
    }

    [Fact]
    public void A_size_given_apart_from_the_name_is_used_and_compared_as_if_the_name_stated_it()
    {
        var product = new ProductFacts("Grated mozzarella 250g", null);

        CandidateMatcher.Evaluate(product, new CandidateFacts("Greenfield grated mozzarella", null, null, new ProductSize(SizeUnit.Mass, 250, 1)))!
            .Basis.Should().Be(MatchBasis.NameAndSize);
        CandidateMatcher.Evaluate(product, new CandidateFacts("Greenfield grated mozzarella", null, null, new ProductSize(SizeUnit.Mass, 500, 1))).Should().BeNull();
        CandidateMatcher.Evaluate(new ProductFacts("Grated mozzarella", null, null, new ProductSize(SizeUnit.Mass, 250, 1)), new CandidateFacts("Grated mozzarella 250g", null))!
            .Basis.Should().Be(MatchBasis.NameAndSize);
    }

    [Fact]
    public void A_barcode_still_decides_before_any_name_or_label()
    {
        const string code = "4006381333931";
        CandidateMatcher.Evaluate(new ProductFacts("Tesco whole milk 2L", code), new CandidateFacts("Something else", "04006381333931", "Frozen"))
            .Should().Be(new MatchVerdict(MatchBasis.Barcode, 100));
    }

    // ---- category agreement -----------------------------------------------------------------------------------

    [Fact]
    public void Categories_that_share_a_word_agree_and_add_to_the_score()
    {
        var plain = Verdict("Whole milk 2L", "Fresh whole milk 2 litres")!;
        var agreeing = Verdict("Whole milk 2L", "Fresh whole milk 2 litres", "Chilled Food > Milk", "Fresh Food > Dairy > Milk")!;

        agreeing.Basis.Should().Be(MatchBasis.NameAndSize);
        agreeing.Score.Should().Be(plain.Score + CandidateMatcher.CategoryBonus);
        Verdict("Tesco whole milk 2L", "Milbona whole milk 2L", "Milk", "Dairy > Milk")!.Score.Should().Be(45 + CandidateMatcher.CategoryBonus);
    }

    [Fact]
    public void Disagreeing_categories_reject_the_loose_matches_but_not_a_name_and_size_one()
    {
        Verdict("Whole milk 2L", "Fresh whole milk 2 litres", "Milk", "Household > Cleaning")!.Basis.Should().Be(MatchBasis.NameAndSize);
        Verdict("Tesco whole milk 2L", "Milbona whole milk 2L", "Milk", "Household > Cleaning").Should().BeNull();
        Verdict("Whole milk", "Whole milk 2L", "Milk", "Household > Cleaning").Should().BeNull();
    }

    [Theory]
    [InlineData(null, "Dairy > Milk")]
    [InlineData("Dairy > Milk", null)]
    [InlineData("", "Dairy > Milk")]
    [InlineData("Fresh Food", "Dairy > Milk")] // nothing but filler words: no opinion either way
    public void A_missing_category_changes_nothing(string? product, string? candidate)
    {
        Verdict("Tesco whole milk 2L", "Milbona whole milk 2L", product, candidate)!.Score.Should().Be(45);
        Verdict("Whole milk", "Whole milk 2L", product, candidate)!.Basis.Should().Be(MatchBasis.NameOnly);
    }

    [Fact]
    public void Plurals_and_accents_do_not_hide_a_category_agreement() =>
        Verdict("Whole milk 2L", "Whole milk 2L", "Crème > Eggs", "Creme > Egg")!.Score.Should().Be(90 + CandidateMatcher.CategoryBonus);

    // ---- narrowing the catalog -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Avonmore Whole Milk 2L", "avonmore")]
    [InlineData("Tesco Whole Milk 2L", "whole")]
    [InlineData("Milk 2L", "milk")]
    [InlineData("Tesco 2L", null)]
    [InlineData("Free range eggs, pack of 12", "free")]
    public void The_key_word_is_the_first_generic_word(string name, string? expected) =>
        CandidateMatcher.KeyWord(name).Should().Be(expected);
}
