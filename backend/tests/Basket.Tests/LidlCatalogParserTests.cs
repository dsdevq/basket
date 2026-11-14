using Basket.Api.Prices.Parsing;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>The Lidl catalog readers on synthetic pages: listing tiles, the page sitemap and the barcode of a product page.</summary>
public sealed class LidlCatalogParserTests
{
    [Fact]
    public void Every_tile_becomes_a_catalog_item_priced_or_not()
    {
        var items = LidlParser.ParseCatalogTiles(Fixtures.Read("lidl-catalog-page-1.html"));

        items.Select(i => i.ExternalId).Should().Equal("90000001", "90000002", "90000003");

        var butter = items[0];
        butter.Url.Should().Be("https://www.lidl.ie/p/dairyfield-unsalted-butter/p90000001");
        butter.Name.Should().Be("DAIRYFIELD Unsalted Butter 250g");
        butter.Brand.Should().Be("DAIRYFIELD");
        butter.SizeText.Should().Be("250g");
        butter.Category.Should().Be("Food and near food > Dairy > Butter", "the root every product shares is dropped");
        butter.ImageUrl.Should().Be("https://www.lidl.ie/assets/synthetic-butter.png");
        butter.Price.Should().Be(2.49m);
        butter.LoyaltyPrice.Should().BeNull();

        var biscuits = items[1];
        biscuits.Price.Should().BeNull("only a Lidl Plus price is shown");
        biscuits.LoyaltyPrice.Should().Be(1.19m);
        biscuits.LoyaltyScheme.Should().Be("Lidl Plus");
        biscuits.PromoText.Should().Be("Lidl Plus deal");

        var tea = items[2];
        tea.Price.Should().BeNull();
        tea.LoyaltyPrice.Should().BeNull("a product with no price at all is still a catalog entry");
    }

    [Fact]
    public void A_tile_without_a_product_number_is_returned_unusable_so_the_caller_can_count_it()
    {
        var items = LidlParser.ParseCatalogTiles(Fixtures.Read("lidl-catalog-page-2.html"));

        items.Select(i => i.ExternalId).Should().Equal("90000003", "90000004", null);
        items[2].Url.Should().Be("https://www.lidl.ie/p/none/p1");
        items[1].Brand.Should().BeNull();
        items[1].Price.Should().Be(1.09m);
    }

    [Fact]
    public void A_page_without_tiles_has_no_items()
    {
        LidlParser.ParseCatalogTiles(Fixtures.Read("lidl-catalog-page-empty.html")).Should().BeEmpty();
        LidlParser.ParseCatalogTiles("not html at all").Should().BeEmpty();
    }

    [Fact]
    public void The_page_sitemap_is_found_in_the_index()
    {
        var locations = LidlParser.SitemapLocations(Fixtures.Read("lidl-sitemap-index.xml"));

        locations.Select(l => l.AbsolutePath).Should().Equal(
            "/p/export/IE/en/product_sitemap.xml.gz", "/static/pages_en-IE_ie.xml.gz", "/static/other_sitemap.xml.gz");
    }

    [Fact]
    public void Food_category_pages_are_picked_from_the_page_sitemap_by_name()
    {
        var pages = LidlParser.FoodCategoryPages(Fixtures.Read("lidl-pages-sitemap.xml"));

        pages.Select(p => p.AbsoluteUri).Should().Equal(
            "https://www.lidl.ie/c/food-drink/s90000100",
            "https://www.lidl.ie/c/fresh-fruit-and-vegetables/s90000101",
            "https://www.lidl.ie/c/chilled-meat-and-fish/s90000105");
    }

    [Fact]
    public void The_first_valid_barcode_of_the_product_page_is_read_as_a_gtin14()
    {
        LidlParser.ParseGtin(Fixtures.Read("lidl-product-page-gtin.html")).Should().Be("04000000000013");
    }

    [Theory]
    [InlineData("lidl-product-page-bad-gtin.html")]
    [InlineData("akamai-access-denied.html")]
    public void A_page_without_a_valid_barcode_has_none(string fixture)
    {
        LidlParser.ParseGtin(Fixtures.Read(fixture)).Should().BeNull();
    }
}
