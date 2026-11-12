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
