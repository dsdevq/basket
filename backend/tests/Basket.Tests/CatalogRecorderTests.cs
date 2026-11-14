using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The shared write path of the combined catalog: what any sweep, server or PC, does to the tables.</summary>
public sealed class CatalogRecorderTests : IClassFixture<BasketFactory>
{
    private static readonly DateTimeOffset Day1 = DateTimeOffset.Parse("2026-11-14T03:00:00Z");

    private readonly BasketFactory _factory;

    public CatalogRecorderTests(BasketFactory factory) => _factory = factory;

    private static CatalogReading Reading(Store store = Store.Lidl, string id = "11214436", Func<CatalogItem, CatalogItem>? change = null)
    {
        var item = new CatalogItem(
            id,
            $"https://www.lidl.ie/p/aubergine/p{id}",
            "Aubergine",
            "Farmers Market",
            "2 pack",
            Barcode: "5012345678900",
            Category: "Fruit & Veg > Vegetables",
            ImageUrl: "https://images.example.test/aubergine.jpg",
            Price: 0.99m);
        CatalogReading.TryCreate(store, change is null ? item : change(item), out var reading, out var reason).Should().BeTrue(reason);
        return reading!;
    }

    private async Task<CatalogRecordResult> RecordAsync(DateTimeOffset at, params CatalogReading[] readings) =>
        await _factory.WithDb(async db =>
        {
            var result = await new CatalogRecorder(db).RecordAsync(readings[0].Store, readings, at, CancellationToken.None);
            await db.SaveChangesAsync();
            return result;
        });

    private async Task CleanAsync() =>
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            return 0;
        });

    private Task<StoreProduct> ProductAsync(Store store = Store.Lidl, string id = "11214436") =>
        _factory.WithDb(db => db.StoreProducts.SingleAsync(p => p.Store == store && p.ExternalId == id));

    private Task<List<StoreProductPrice>> PricesAsync() =>
        _factory.WithDb(db => db.StoreProductPrices.OrderBy(p => p.ObservedOn).ToListAsync());

    [Fact]
    public async Task A_new_product_is_inserted_with_its_normalised_fields_and_first_price()
    {
        await CleanAsync();

        var result = await RecordAsync(Day1, Reading(change: i => i with { SizeText = "6 x 330ml", UnitPrice = 0.5m, UnitPriceBasis = "1 L" }));

        result.Should().Be(new CatalogRecordResult(1, 0, 1));
        var product = await ProductAsync();
        product.Name.Should().Be("Aubergine");
        product.Brand.Should().Be("Farmers Market");
        product.Url.Should().Be("https://www.lidl.ie/p/aubergine/p11214436");
        product.SizeText.Should().Be("6 x 330ml");
        (product.SizeAmount, product.SizeUnit, product.SizePacks).Should().Be((330m, "ml", 6));
        (product.UnitPrice, product.UnitPriceBasis).Should().Be((0.5m, "1 L"));
        product.Barcode.Should().Be("05012345678900");
        product.Category.Should().Be("Fruit & Veg > Vegetables");
        product.ImageUrl.Should().Be("https://images.example.test/aubergine.jpg");
        product.FirstSeenAt.Should().Be(Day1);
        product.LastSeenAt.Should().Be(Day1);
        product.DiscontinuedAt.Should().BeNull();
        var price = (await PricesAsync()).Should().ContainSingle().Subject;
        price.ObservedOn.Should().Be(PriceRecorder.DublinDay(Day1));
        price.Price.Should().Be(0.99m);
    }

    [Fact]
    public async Task A_product_seen_again_is_updated_in_place_never_duplicated()
    {
        await CleanAsync();
        await RecordAsync(Day1, Reading());

        var again = await RecordAsync(Day1.AddDays(1), Reading(change: i => i with { Name = "Aubergine Large" }));

        again.Should().Be(new CatalogRecordResult(0, 1, 0));
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(1);
        var product = await ProductAsync();
        product.Name.Should().Be("Aubergine Large");
        product.FirstSeenAt.Should().Be(Day1);
        product.LastSeenAt.Should().Be(Day1.AddDays(1));
    }

    [Fact]
    public async Task The_same_id_in_two_shops_is_two_products()
    {
        await CleanAsync();

        await RecordAsync(Day1, Reading(Store.Lidl, "123456"));
        await RecordAsync(Day1, Reading(Store.Aldi, "123456", i => i with { Url = "https://www.aldi.ie/product/aubergine-123456" }));

        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task A_price_row_is_written_only_when_the_price_tuple_changes()
    {
        await CleanAsync();
        await RecordAsync(Day1, Reading());

        await RecordAsync(Day1.AddDays(1), Reading());
        await RecordAsync(Day1.AddDays(2), Reading());
        (await PricesAsync()).Should().HaveCount(1);

        await RecordAsync(Day1.AddDays(3), Reading(change: i => i with { Price = 1.09m }));
        await RecordAsync(Day1.AddDays(4), Reading(change: i => i with { Price = 1.09m }));
        await RecordAsync(Day1.AddDays(5), Reading(change: i => i with { Price = 1.09m, WasPrice = 1.29m, PromoText = "Price drop" }));

        var rows = await PricesAsync();
        rows.Select(r => (r.ObservedOn, r.Price, r.WasPrice)).Should().Equal(
            (PriceRecorder.DublinDay(Day1), 0.99m, (decimal?)null),
            (PriceRecorder.DublinDay(Day1.AddDays(3)), 1.09m, null),
            (PriceRecorder.DublinDay(Day1.AddDays(5)), 1.09m, 1.29m));
        (await ProductAsync()).LastSeenAt.Should().Be(Day1.AddDays(5));
    }

    [Fact]
    public async Task A_different_reading_on_the_same_dublin_day_replaces_that_days_row()
    {
        await CleanAsync();
        await RecordAsync(Day1, Reading());

        await RecordAsync(Day1.AddHours(2), Reading(change: i => i with { Price = 0.89m }));

        (await PricesAsync()).Should().ContainSingle().Which.Price.Should().Be(0.89m);
    }

    [Fact]
    public async Task A_product_without_a_price_gets_no_price_row_but_a_vanished_price_is_recorded()
    {
        await CleanAsync();

        await RecordAsync(Day1, Reading(change: i => i with { Price = null }));
        (await PricesAsync()).Should().BeEmpty();
        (await ProductAsync()).Name.Should().Be("Aubergine");

        await RecordAsync(Day1.AddDays(1), Reading(change: i => i with { Price = 0.99m }));
        await RecordAsync(Day1.AddDays(2), Reading(change: i => i with { Price = null }));
        await RecordAsync(Day1.AddDays(3), Reading(change: i => i with { Price = null }));

        (await PricesAsync()).Select(r => r.Price).Should().Equal(0.99m, null);
    }

    [Fact]
    public async Task A_thinner_reading_never_erases_a_barcode_a_photo_a_brand_or_a_category()
    {
        await CleanAsync();
        await RecordAsync(Day1, Reading());

        await RecordAsync(Day1.AddDays(1), Reading(change: i => i with { Barcode = null, ImageUrl = null, Brand = null, Category = null, SizeText = "3 pack" }));

        var product = await ProductAsync();
        product.Barcode.Should().Be("05012345678900");
        product.ImageUrl.Should().Be("https://images.example.test/aubergine.jpg");
        product.Brand.Should().Be("Farmers Market");
        product.Category.Should().Be("Fruit & Veg > Vegetables");
        product.SizeText.Should().Be("3 pack");
    }

    [Fact]
    public async Task A_discontinued_product_that_is_listed_again_comes_back()
    {
        await CleanAsync();
        await RecordAsync(Day1, Reading());
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteUpdateAsync(s => s.SetProperty(p => p.DiscontinuedAt, Day1.AddDays(1)));
            return 0;
        });

        await RecordAsync(Day1.AddDays(2), Reading());

        (await ProductAsync()).DiscontinuedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_same_id_twice_in_one_call_leaves_one_product_with_the_last_reading()
    {
        await CleanAsync();

        await RecordAsync(Day1, Reading(), Reading(change: i => i with { Price = 1.19m }));

        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(1);
        (await PricesAsync()).Should().ContainSingle().Which.Price.Should().Be(1.19m);
    }

    [Fact]
    public async Task Full_text_search_finds_a_product_by_name_or_brand_through_the_generated_column()
    {
        await CleanAsync();
        await RecordAsync(Day1, Reading(id: "111111"), Reading(id: "222222", change: i => i with { Name = "Grated Mozzarella", Brand = "Greenfield", Price = 1.49m }));

        var byName = await _factory.WithDb(db => db.StoreProducts
            .Where(p => p.SearchVector.Matches(EF.Functions.PlainToTsQuery("english", "mozzarella"))).Select(p => p.ExternalId).ToListAsync());
        var byBrand = await _factory.WithDb(db => db.StoreProducts
            .Where(p => p.SearchVector.Matches(EF.Functions.PlainToTsQuery("english", "greenfield"))).Select(p => p.ExternalId).ToListAsync());
        var stemmed = await _factory.WithDb(db => db.StoreProducts
            .Where(p => p.SearchVector.Matches(EF.Functions.PlainToTsQuery("english", "aubergines"))).Select(p => p.ExternalId).ToListAsync());

        byName.Should().Equal("222222");
        byBrand.Should().Equal("222222");
        stemmed.Should().Equal("111111");
    }

    [Fact]
    public async Task Deleting_a_catalog_product_clears_the_link_on_a_listing_and_keeps_the_listing()
    {
        await CleanAsync();
        await _factory.OwnerAsync();
        await RecordAsync(Day1, Reading(Store.Tesco, "254317390", i => i with { Url = "https://www.tesco.ie/groceries/en-IE/products/254317390" }));
        var listingId = await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Aubergine", AddedAt = Day1, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            var catalog = await db.StoreProducts.SingleAsync();
            var listing = new StoreListing
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                Store = Store.Tesco,
                Url = catalog.Url,
                AddedAt = Day1,
                StoreProductId = catalog.Id,
            };
            db.Products.Add(product);
            db.StoreListings.Add(listing);
            await db.SaveChangesAsync();
            return listing.Id;
        });

        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            return 0;
        });

        (await _factory.WithDb(db => db.StoreListings.Where(l => l.Id == listingId).Select(l => l.StoreProductId).SingleAsync())).Should().BeNull();
    }
}
