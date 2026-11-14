using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>What the catalog accepts from a reader, and the shapes it turns an item into (no database).</summary>
public sealed class CatalogReadingTests
{
    private const string AldiUrl = "https://www.aldi.ie/product/greenfield-grated-mozzarella-000000000000900101";

    private static CatalogItem Item(Func<CatalogItem, CatalogItem>? change = null)
    {
        var item = new CatalogItem("000000000000900101", AldiUrl, "Grated Mozzarella", "GREENFIELD", "250 g", Price: 1.49m);
        return change is null ? item : change(item);
    }

    private static string? Reason(Store store, CatalogItem item) =>
        CatalogReading.TryCreate(store, item, out _, out var reason) ? null : reason;

    private static CatalogReading Read(CatalogItem item)
    {
        CatalogReading.TryCreate(Store.Aldi, item, out var reading, out var reason).Should().BeTrue(reason);
        return reading!;
    }

    [Fact]
    public void A_good_item_becomes_a_reading_with_its_size_barcode_and_photo_in_the_shapes_the_tables_hold()
    {
        var reading = Read(Item(i => i with
        {
            SizeText = "1.5L",
            Barcode = "5012345678900",
            ImageUrl = "https://images.example.test/a.jpg",
            UnitPrice = 0.99m,
            UnitPriceBasis = "1 L",
        }));

        reading.Size.Should().Be(new ProductSize(SizeUnit.Volume, 1500m, 1));
        reading.Barcode.Should().Be("05012345678900");
        reading.ImageUrl.Should().Be("https://images.example.test/a.jpg");
        reading.Url.Should().Be(AldiUrl);
        reading.UnitPrice.Should().Be(0.99m);
        reading.UnitPriceBasis.Should().Be("1 L");
        reading.Price.InStock.Should().BeTrue();
    }

    [Fact]
    public void The_size_falls_back_to_the_name_and_a_multipack_keeps_its_pack_count()
    {
        Read(Item(i => i with { SizeText = null, Name = "Sparkling Water 6 x 330ml" })).Size
            .Should().Be(new ProductSize(SizeUnit.Volume, 330m, 6));
        Read(Item(i => i with { SizeText = null, Name = "Grated Mozzarella" })).Size.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_item_without_an_id_is_refused(string? id) =>
        Reason(Store.Aldi, Item(i => i with { ExternalId = id })).Should().Be("external_id_invalid");

    [Fact]
    public void An_id_longer_than_the_column_is_refused_rather_than_cut() =>
        Reason(Store.Aldi, Item(i => i with { ExternalId = new string('1', StoreProduct.MaxExternalIdLength + 1) })).Should().Be("external_id_invalid");

    [Theory]
    [InlineData("http://www.aldi.ie/product/x-000000000000900101")]
    [InlineData("https://www.tesco.ie/groceries/en-IE/products/254317390")]
    [InlineData("https://evil.example/product/x-000000000000900101")]
    [InlineData(null)]
    public void An_address_that_is_not_the_shops_product_page_is_refused(string? url) =>
        Reason(Store.Aldi, Item(i => i with { Url = url })).Should().Be("url_invalid");

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void An_item_without_a_name_is_refused(string? name) =>
        Reason(Store.Aldi, Item(i => i with { Name = name })).Should().Be("no_name");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    [InlineData(1.499)]
    public void A_price_that_is_not_a_euro_amount_refuses_the_item(double price)
    {
        Reason(Store.Aldi, Item(i => i with { Price = (decimal)price })).Should().Be("price_invalid");
        Reason(Store.Aldi, Item(i => i with { WasPrice = (decimal)price })).Should().Be("price_invalid");
        Reason(Store.Aldi, Item(i => i with { LoyaltyPrice = (decimal)price })).Should().Be("price_invalid");
        Reason(Store.Aldi, Item(i => i with { UnitPrice = (decimal)price })).Should().Be("price_invalid");
    }

    [Fact]
    public void Dates_that_run_backwards_refuse_the_item() =>
        Reason(Store.Aldi, Item(i => i with { ValidFrom = new DateOnly(2026, 11, 20), ValidUntil = new DateOnly(2026, 11, 10) })).Should().Be("dates_invalid");

    [Fact]
    public void An_item_with_no_price_at_all_is_a_catalog_entry_not_an_error()
    {
        var reading = Read(Item(i => i with { Price = null }));

        reading.Price.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Out_of_stock_is_a_price_statement_even_without_a_price() =>
        Read(Item(i => i with { Price = null, InStock = false })).Price.IsEmpty.Should().BeFalse();

    [Theory]
    [InlineData("5012345678901")]
    [InlineData("not a barcode")]
    [InlineData("123")]
    public void A_barcode_that_is_not_a_valid_gtin_is_dropped_and_the_product_is_kept(string barcode) =>
        Read(Item(i => i with { Barcode = barcode })).Barcode.Should().BeNull();

    [Theory]
    [InlineData("http://images.example.test/a.jpg")]
    [InlineData("https://user:pw@images.example.test/a.jpg")]
    [InlineData("/relative.jpg")]
    public void A_photo_address_that_is_not_a_plain_https_one_is_dropped_and_the_product_is_kept(string image) =>
        Read(Item(i => i with { ImageUrl = image })).ImageUrl.Should().BeNull();

    [Fact]
    public void A_loyalty_price_takes_the_shops_own_scheme_when_the_item_names_none()
    {
        CatalogReading.TryCreate(
            Store.Tesco,
            new CatalogItem("254317390", "https://www.tesco.ie/groceries/en-IE/products/254317390", "Milk 2L", LoyaltyPrice: 1.89m),
            out var reading,
            out _).Should().BeTrue();

        reading!.Price.LoyaltyScheme.Should().Be("Clubcard");
    }

    [Fact]
    public void Text_is_cleaned_and_cut_to_the_columns()
    {
        var reading = Read(Item(i => i with
        {
            Name = "  Grated\t Mozzarella\n",
            Brand = new string('b', 500),
            Category = "Chilled Food >  Cheese",
        }));

        reading.Name.Should().Be("Grated Mozzarella");
        reading.Brand.Should().HaveLength(StoreProduct.MaxBrandLength);
        reading.Category.Should().Be("Chilled Food > Cheese");
    }
}
