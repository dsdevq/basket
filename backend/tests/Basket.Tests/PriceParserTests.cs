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
        skewers.ValidFrom.Should().Be(new DateOnly(2026, 8, 26));
        skewers.ValidUntil.Should().Be(new DateOnly(2026, 11, 11));
        skewers.InStock.Should().BeTrue();
    }

    [Fact]
    public void Lidl_offer_window_is_the_prices_own_not_the_tiles_store_dates()
    {
        // Oracle: regionsPrices.1 in the recorded tiles. The skewers' 2.19 runs 26 Aug - 11 Nov while the tile's
        // storeStartDate is 12 Nov, so on 5 Nov it must be active, never "upcoming".
        var today = new DateOnly(2026, 11, 5);
        var skewers = Offers["11115738"];
        (skewers.ValidFrom <= today && today <= skewers.ValidUntil).Should().BeTrue();

        Offers["11272692"].ValidFrom.Should().Be(new DateOnly(2026, 11, 3));
        Offers["11272692"].ValidUntil.Should().Be(new DateOnly(2026, 12, 31));
        Offers["11101814"].ValidFrom.Should().Be(new DateOnly(2026, 10, 28));
        Offers["11101814"].ValidUntil.Should().Be(new DateOnly(2026, 11, 11));
        Offers["11121624"].ValidFrom.Should().Be(new DateOnly(2026, 11, 3));
        Offers["11121624"].ValidUntil.Should().Be(new DateOnly(2026, 12, 31));
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
