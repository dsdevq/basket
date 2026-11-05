using Basket.Api.Prices;
using Basket.Api.Prices.Parsing;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>The parsers against pages recorded from lidl.ie and aldi.ie on 2026-11-05. No test touches the network.</summary>
public sealed class PriceParserTests
{
    private static readonly IReadOnlyDictionary<string, PriceQuote> Offers = LidlParser.ParseOfferTiles(Fixtures.Read("lidl-offers-page.html"));

    [Fact]
    public void Lidl_offer_tile_gives_price_lidl_plus_price_promo_and_dates()
    {
        var skewers = Offers["11115738"];

        skewers.Title.Should().Be("VITASIA 10 Chicken Breast Skewers");
        skewers.Price.Should().Be(2.19m);
        skewers.LoyaltyPrice.Should().Be(2.19m);
        skewers.LoyaltyScheme.Should().Be("Lidl Plus");
        skewers.PromoText.Should().Be("Mix 'n' Match 2 for €4");
        skewers.ValidFrom.Should().Be(new DateOnly(2026, 11, 12));
        skewers.ValidUntil.Should().Be(new DateOnly(2027, 1, 1));
        skewers.InStock.Should().BeTrue();
    }

    [Fact]
    public void Lidl_plus_only_offer_has_no_shelf_price_and_keeps_the_old_price()
    {
        var tree = Offers["11101814"];

        tree.Price.Should().BeNull();
        tree.LoyaltyPrice.Should().Be(39.99m);
        tree.WasPrice.Should().Be(49.99m);
        tree.PromoText.Should().Be("-20%");
        Offers["11272692"].LoyaltyPrice.Should().Be(2.49m);
    }

    [Fact]
    public void Lidl_food_tile_without_lidl_plus_has_just_the_price()
    {
        var cranberry = Offers["11121624"];

        cranberry.Price.Should().Be(1.69m);
        cranberry.LoyaltyPrice.Should().BeNull();
        cranberry.LoyaltyScheme.Should().BeNull();
        Offers["11039946"].Price.Should().Be(3.09m);
        Offers.Should().HaveCount(5);
    }

    [Fact]
    public void Lidl_page_without_tiles_gives_nothing()
    {
        LidlParser.ParseOfferTiles("<html><body>no grid here</body></html>").Should().BeEmpty();
        LidlParser.ParseOfferTiles("<div data-grid-data=\"{not json\"></div>").Should().BeEmpty();
    }

    [Fact]
    public void Lidl_product_page_reads_the_json_ld_price_and_stock()
    {
        var priced = LidlParser.ParseProductPage(Fixtures.Read("lidl-product-page.html"));
        priced.Title.Should().Be("10 Chicken Breast Skewers");
        priced.Price.Should().Be(2.19m);
        priced.InStock.Should().BeFalse("the page says OutOfStock");

        var unpriced = LidlParser.ParseProductPage(Fixtures.Read("lidl-product-page-no-price.html"));
        unpriced.Title.Should().Be("Aubergine");
        unpriced.Price.Should().BeNull();
        unpriced.InStock.Should().BeFalse();
    }

    [Fact]
    public void Lidl_product_page_without_product_data_is_a_parse_failure()
    {
        var act = () => LidlParser.ParseProductPage("<html><body>Something else</body></html>");

        act.Should().Throw<ParseException>();
    }

    [Fact]
    public void Aldi_discounted_product_gives_the_price_charged_the_original_price_and_the_tag()
    {
        var quote = AldiParser.ParseProductPage(Fixtures.Read("aldi-product-discounted.html"));

        quote.Title.Should().Be("ACTILEAF UHT Coconut Milk");
        quote.Price.Should().Be(1.39m);
        quote.WasPrice.Should().Be(1.49m);
        quote.PromoText.Should().Be("Save 6%");
        quote.LoyaltyPrice.Should().BeNull();
        quote.InStock.Should().BeTrue();
    }

    [Fact]
    public void Aldi_regular_product_has_no_reduction()
    {
        var quote = AldiParser.ParseProductPage(Fixtures.Read("aldi-product-regular.html"));

        quote.Title.Should().Be("CLONBAWN Protein Milk");
        quote.Price.Should().Be(1.49m);
        quote.WasPrice.Should().BeNull();
        quote.PromoText.Should().BeNull();
    }

    [Fact]
    public void Aldi_page_without_product_data_is_a_parse_failure()
    {
        var act = () => AldiParser.ParseProductPage(Fixtures.Read("akamai-access-denied.html"));

        act.Should().Throw<ParseException>();
    }

    [Theory]
    [InlineData(Store.Lidl, "https://www.lidl.ie/p/aubergine/p11214436", "https://www.lidl.ie/p/aubergine/p11214436")]
    [InlineData(Store.Lidl, "https://lidl.ie/p/aubergine/p11214436/?utm_source=x#top", "https://www.lidl.ie/p/aubergine/p11214436")]
    [InlineData(Store.Aldi, " https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541 ", "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541")]
    [InlineData(Store.Tesco, "https://www.tesco.ie/groceries/en-IE/products/254317390", "https://www.tesco.ie/groceries/en-IE/products/254317390")]
    public void Product_addresses_on_the_shops_own_site_are_accepted_and_tidied(Store store, string raw, string expected)
    {
        Stores.TryNormalizeProductUrl(store, raw, out var url).Should().BeTrue();

        url.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(Store.Lidl, "http://www.lidl.ie/p/aubergine/p11214436")]
    [InlineData(Store.Lidl, "https://www.lidl.co.uk/p/aubergine/p11214436")]
    [InlineData(Store.Lidl, "https://www.lidl.ie.evil.example/p/aubergine/p11214436")]
    [InlineData(Store.Lidl, "https://user@www.lidl.ie:8443/p/aubergine/p11214436")]
    [InlineData(Store.Lidl, "https://www.lidl.ie/c/food-drink/s10068374")]
    [InlineData(Store.Lidl, "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541")]
    [InlineData(Store.Aldi, "https://www.aldi.ie/results?q=milk")]
    [InlineData(Store.Tesco, "https://www.tesco.ie/groceries/en-IE/search?query=milk")]
    [InlineData(Store.Tesco, "not a url")]
    [InlineData(Store.Tesco, "")]
    public void Anything_else_is_refused(Store store, string raw)
    {
        Stores.TryNormalizeProductUrl(store, raw, out _).Should().BeFalse();
    }
}
