using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Data.Migrations;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// The join between the shelf and the combined catalog: a listing finds its catalog row by the shop's product id in its address,
/// and every catalog reading of a linked page is mirrored into the shelf's price history. Synthetic products only.
/// </summary>
public sealed class CatalogLinkTests : IClassFixture<BasketFactory>
{
    private const string TescoUrl = "https://www.tesco.ie/groceries/en-IE/products/254300001";
    private const string AldiUrl = "https://www.aldi.ie/product/greenfield-grated-mozzarella-000000000000900101";

    private static readonly DateTimeOffset Day1 = DateTimeOffset.Parse("2026-11-14T10:00:00Z");

    private readonly BasketFactory _factory;

    public CatalogLinkTests(BasketFactory factory) => _factory = factory;

    // ---- the id in the address ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(Store.Lidl, "https://www.lidl.ie/p/aubergine/p11214436", "11214436")]
    [InlineData(Store.Tesco, "https://www.tesco.ie/groceries/en-IE/products/254317390", "254317390")]
    [InlineData(Store.Tesco, "https://www.tesco.ie/shop/en-IE/products/254317390/", "254317390")]
    [InlineData(Store.Aldi, "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541", "000000000000422541")]
    [InlineData(Store.Dunnes, "https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581", "100317581")]
    [InlineData(Store.Aldi, "https://www.aldi.ie/product/oakridge-sparkling-water-6-x-330ml-000000000000900103", "000000000000900103")]
    public void The_shops_own_product_id_is_read_off_the_address(Store store, string url, string expected) =>
        Stores.ProductId(store, url).Should().Be(expected);

    [Theory]
    [InlineData(Store.Lidl, "https://www.lidl.ie/p/aubergine")]
    [InlineData(Store.Lidl, "https://www.tesco.ie/groceries/en-IE/products/254317390")]
    [InlineData(Store.Tesco, "https://www.tesco.ie/groceries/en-IE/products/abc")]
    [InlineData(Store.Aldi, "https://www.aldi.ie/product/no-number-here")]
    [InlineData(Store.Dunnes, "not an address")]
    [InlineData(Store.Dunnes, null)]
    public void An_address_that_is_not_a_product_page_of_the_shop_has_no_id(Store store, string? url) =>
        Stores.ProductId(store, url).Should().BeNull();

    // ---- fixtures ----------------------------------------------------------------------------------------------

    private async Task<HttpClient> CleanAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            await db.StoreProducts.ExecuteDeleteAsync();
            return 0;
        });
        return owner;
    }

    private async Task<Guid> ProductAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/products", new { name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static CatalogReading Reading(Store store, string id, string url, decimal? price = 1.49m, Func<CatalogItem, CatalogItem>? change = null)
    {
        var item = new CatalogItem(id, url, "Grated Mozzarella", "Greenfield", "250 g", Price: price, ImageUrl: "https://images.example.test/mozzarella.jpg", Category: "Chilled Food > Cheese");
        CatalogReading.TryCreate(store, change is null ? item : change(item), out var reading, out var reason).Should().BeTrue(reason);
        return reading!;
    }

    private Task RecordAsync(DateTimeOffset at, params CatalogReading[] readings) =>
        _factory.WithDb(async db =>
        {
            await new CatalogRecorder(db, new PriceRecorder(db)).RecordAsync(readings[0].Store, readings, at, CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

    private Task<Guid> AddListingAsync(Guid productId, Store store, string url, DateTimeOffset? confirmedAt = null) =>
        _factory.WithDb(async db =>
        {
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = productId, Store = store, Url = url, AddedAt = Day1, ConfirmedAt = confirmedAt };
            db.StoreListings.Add(listing);
            await db.SaveChangesAsync();
            return listing.Id;
        });

    private Task<StoreListing> ListingAsync(Guid id) => _factory.WithDb(db => db.StoreListings.AsNoTracking().SingleAsync(l => l.Id == id));

    private Task<Guid?> CatalogIdAsync(Store store, string externalId) =>
        _factory.WithDb(db => db.StoreProducts.Where(p => p.Store == store && p.ExternalId == externalId).Select(p => (Guid?)p.Id).SingleOrDefaultAsync());

    private Task<List<PriceObservation>> ObservationsAsync(Guid listingId) =>
        _factory.WithDb(db => db.PriceObservations.Where(o => o.ListingId == listingId).OrderBy(o => o.ObservedOn).ToListAsync());

    // ---- going forward: set on link ----------------------------------------------------------------------------

    [Fact]
    public async Task Pasting_a_page_the_catalog_has_links_the_listing_to_it_and_confirms_it_as_before()
    {
        var owner = await CleanAsync();
        await RecordAsync(Day1, Reading(Store.Tesco, "254300001", TescoUrl));
        var product = await ProductAsync(owner, "Mozzarella");

        (await owner.PutAsJsonAsync($"/api/products/{product}/stores/tesco", new { url = TescoUrl })).StatusCode.Should().Be(HttpStatusCode.OK);

        var listing = await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == product));
        listing.StoreProductId.Should().Be(await CatalogIdAsync(Store.Tesco, "254300001"));
        listing.ConfirmedAt.Should().NotBeNull("a person pasting a page still confirms it");
    }

    [Fact]
    public async Task A_page_the_catalog_does_not_have_yet_stays_unlinked_to_it()
    {
        var owner = await CleanAsync();
        var product = await ProductAsync(owner, "Mozzarella");

        (await owner.PutAsJsonAsync($"/api/products/{product}/stores/tesco", new { url = TescoUrl })).EnsureSuccessStatusCode();

        (await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == product))).StoreProductId.Should().BeNull();
    }

    [Fact]
    public async Task Relinking_to_another_page_moves_the_catalog_link_and_a_page_with_no_catalog_row_clears_it()
    {
        var owner = await CleanAsync();
        const string otherUrl = "https://www.tesco.ie/groceries/en-IE/products/254300002";
        await RecordAsync(Day1, Reading(Store.Tesco, "254300001", TescoUrl), Reading(Store.Tesco, "254300002", otherUrl));
        var product = await ProductAsync(owner, "Mozzarella");

        (await owner.PutAsJsonAsync($"/api/products/{product}/stores/tesco", new { url = TescoUrl })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/products/{product}/stores/tesco", new { url = otherUrl })).EnsureSuccessStatusCode();
        (await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == product))).StoreProductId
            .Should().Be(await CatalogIdAsync(Store.Tesco, "254300002"));

        (await owner.PutAsJsonAsync($"/api/products/{product}/stores/tesco", new { url = "https://www.tesco.ie/groceries/en-IE/products/254399999" })).EnsureSuccessStatusCode();
        (await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == product))).StoreProductId.Should().BeNull();
    }

    [Fact]
    public async Task A_page_the_fleet_links_is_linked_to_the_catalog_and_stays_unconfirmed()
    {
        var owner = await CleanAsync();
        await RecordAsync(Day1, Reading(Store.Tesco, "254300001", TescoUrl));
        var product = await ProductAsync(owner, "Mozzarella");
        var fleet = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        fleet.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Fleet.Mint("fleet:read basket:catalog-add"));

        (await fleet.PutAsJsonAsync($"/api/products/{product}/stores/tesco", new { url = TescoUrl })).StatusCode.Should().Be(HttpStatusCode.OK);

        var listing = await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == product));
        listing.StoreProductId.Should().Be(await CatalogIdAsync(Store.Tesco, "254300001"));
        listing.ConfirmedAt.Should().BeNull();
    }

    // ---- the migration's fill ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_migration_fill_links_existing_listings_by_the_id_in_their_address_and_leaves_the_rest()
    {
        var owner = await CleanAsync();
        await RecordAsync(Day1, Reading(Store.Tesco, "254300001", TescoUrl));
        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl));
        await RecordAsync(Day1, Reading(Store.Lidl, "11214436", "https://www.lidl.ie/p/aubergine/p11214436"));
        await RecordAsync(Day1, Reading(Store.Dunnes, "100317581", "https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581"));
        var product = await ProductAsync(owner, "Mozzarella");
        var other = await ProductAsync(owner, "Swede");
        var third = await ProductAsync(owner, "Aubergine");
        var fourth = await ProductAsync(owner, "Milk");
        var tesco = await AddListingAsync(product, Store.Tesco, TescoUrl);
        var aldi = await AddListingAsync(other, Store.Aldi, AldiUrl);
        var lidl = await AddListingAsync(third, Store.Lidl, "https://www.lidl.ie/p/aubergine/p11214436");
        var dunnes = await AddListingAsync(fourth, Store.Dunnes, "https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581", confirmedAt: Day1);
        var unknown = await AddListingAsync(await ProductAsync(owner, "Rice"), Store.Tesco, "https://www.tesco.ie/groceries/en-IE/products/254399999");
        var odd = await AddListingAsync(await ProductAsync(owner, "Flour"), Store.Tesco, "https://example.test/not-a-shop-page");

        await _factory.WithDb(db => db.Database.ExecuteSqlRawAsync(StoreListingCatalogLink.LinkSql));

        (await ListingAsync(tesco)).StoreProductId.Should().Be(await CatalogIdAsync(Store.Tesco, "254300001"));
        (await ListingAsync(aldi)).StoreProductId.Should().Be(await CatalogIdAsync(Store.Aldi, "000000000000900101"));
        (await ListingAsync(lidl)).StoreProductId.Should().Be(await CatalogIdAsync(Store.Lidl, "11214436"));
        var linkedDunnes = await ListingAsync(dunnes);
        linkedDunnes.StoreProductId.Should().Be(await CatalogIdAsync(Store.Dunnes, "100317581"));
        linkedDunnes.ConfirmedAt.Should().Be(Day1, "the fill never changes confirmed_at");
        (await ListingAsync(unknown)).StoreProductId.Should().BeNull();
        (await ListingAsync(odd)).StoreProductId.Should().BeNull();
    }

    [Fact]
    public async Task The_migration_fill_is_safe_with_an_empty_catalog_and_when_run_again()
    {
        var owner = await CleanAsync();
        var listing = await AddListingAsync(await ProductAsync(owner, "Mozzarella"), Store.Tesco, TescoUrl);

        await _factory.WithDb(db => db.Database.ExecuteSqlRawAsync(StoreListingCatalogLink.LinkSql));
        (await ListingAsync(listing)).StoreProductId.Should().BeNull("no catalog row, nothing to link");

        await RecordAsync(Day1, Reading(Store.Tesco, "254300001", TescoUrl));
        await _factory.WithDb(db => db.Database.ExecuteSqlRawAsync(StoreListingCatalogLink.LinkSql));
        var first = (await ListingAsync(listing)).StoreProductId;
        await _factory.WithDb(db => db.Database.ExecuteSqlRawAsync(StoreListingCatalogLink.LinkSql));

        first.Should().NotBeNull();
        (await ListingAsync(listing)).StoreProductId.Should().Be(first);
    }

    // ---- the mirror --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reading_of_a_linked_page_becomes_that_days_observation_and_the_shelf_shows_it()
    {
        var owner = await CleanAsync();
        var product = await ProductAsync(owner, "Mozzarella");
        await RecordAsync(Day1.AddDays(-1), Reading(Store.Aldi, "000000000000900101", AldiUrl, price: 1.59m));
        var catalogId = (await CatalogIdAsync(Store.Aldi, "000000000000900101"))!.Value;
        var listing = await AddListingAsync(product, Store.Aldi, AldiUrl);
        await _factory.WithDb(db => db.Database.ExecuteSqlRawAsync(StoreListingCatalogLink.LinkSql));

        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl, change: i => i with { Price = 1.49m, WasPrice = 1.59m, PromoText = "Special Buy" }));

        var observations = await ObservationsAsync(listing);
        var seen = observations.Should().ContainSingle().Subject;
        seen.ObservedOn.Should().Be(PriceRecorder.DublinDay(Day1));
        (seen.Price, seen.WasPrice, seen.PromoText, seen.InStock).Should().Be((1.49m, 1.59m, "Special Buy", true));
        var row = await ListingAsync(listing);
        row.StoreProductId.Should().Be(catalogId);
        row.LastCheckedAt.Should().Be(Day1);
        row.Title.Should().Be("Grated Mozzarella");
        row.ImageUrl.Should().Be("https://images.example.test/mozzarella.jpg");
        row.Category.Should().Be("Chilled Food > Cheese");
        row.ConfirmedAt.Should().BeNull("a sweep never confirms a match");

        var entry = (await owner.GetFromJsonAsync<JsonElement>("/api/products")).GetProperty("products").EnumerateArray()
            .Single().GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "aldi");
        entry.GetProperty("price").GetProperty("price").GetDecimal().Should().Be(1.49m);
    }

    [Fact]
    public async Task A_second_reading_the_same_day_replaces_the_first_and_the_next_day_adds_a_row()
    {
        var owner = await CleanAsync();
        var product = await ProductAsync(owner, "Mozzarella");
        await RecordAsync(Day1.AddDays(-1), Reading(Store.Aldi, "000000000000900101", AldiUrl));
        var listing = await AddListingAsync(product, Store.Aldi, AldiUrl);
        await _factory.WithDb(db => db.Database.ExecuteSqlRawAsync(StoreListingCatalogLink.LinkSql));

        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl, price: 1.49m));
        await RecordAsync(Day1.AddHours(2), Reading(Store.Aldi, "000000000000900101", AldiUrl, price: 1.39m));
        await RecordAsync(Day1.AddDays(1), Reading(Store.Aldi, "000000000000900101", AldiUrl, price: 1.39m));

        (await ObservationsAsync(listing)).Select(o => (o.ObservedOn, o.Price)).Should().Equal(
            (PriceRecorder.DublinDay(Day1), 1.39m),
            (PriceRecorder.DublinDay(Day1.AddDays(1)), 1.39m));
    }

    [Fact]
    public async Task A_page_whose_catalog_row_arrives_with_the_sweep_is_linked_and_fed_in_the_same_batch()
    {
        var owner = await CleanAsync();
        var product = await ProductAsync(owner, "Mozzarella");
        var listing = await AddListingAsync(product, Store.Aldi, AldiUrl);

        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl, price: 1.49m));

        (await ListingAsync(listing)).StoreProductId.Should().Be(await CatalogIdAsync(Store.Aldi, "000000000000900101"));
        (await ObservationsAsync(listing)).Should().ContainSingle().Which.Price.Should().Be(1.49m);
    }

    [Fact]
    public async Task Every_listing_that_is_the_product_is_fed_and_other_pages_and_shops_are_not()
    {
        var owner = await CleanAsync();
        var first = await AddListingAsync(await ProductAsync(owner, "Mozzarella"), Store.Aldi, AldiUrl);
        var second = await AddListingAsync(await ProductAsync(owner, "Mozzarella block"), Store.Aldi, AldiUrl);
        var elsewhere = await AddListingAsync(await ProductAsync(owner, "Cheese slices"), Store.Aldi, "https://www.aldi.ie/product/greenfield-cheese-slices-000000000000900102");
        var otherShop = await AddListingAsync(await ProductAsync(owner, "Mozzarella at Dunnes"), Store.Dunnes, "https://www.dunnesstoresgrocery.com/product/mozzarella-000000000000900101");

        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl));

        (await ObservationsAsync(first)).Should().ContainSingle();
        (await ObservationsAsync(second)).Should().ContainSingle();
        (await ObservationsAsync(elsewhere)).Should().BeEmpty();
        (await ObservationsAsync(otherShop)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reading_with_no_price_that_is_in_stock_is_kept_in_the_catalog_but_not_mirrored_and_an_unavailable_one_is()
    {
        var owner = await CleanAsync();
        var listing = await AddListingAsync(await ProductAsync(owner, "Mozzarella"), Store.Aldi, AldiUrl);

        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl, price: null));
        (await ObservationsAsync(listing)).Should().BeEmpty();
        (await CatalogIdAsync(Store.Aldi, "000000000000900101")).Should().NotBeNull();

        await RecordAsync(Day1.AddDays(1), Reading(Store.Aldi, "000000000000900101", AldiUrl, price: null, change: i => i with { InStock = false }));
        var seen = (await ObservationsAsync(listing)).Should().ContainSingle().Subject;
        (seen.Price, seen.InStock).Should().Be(((decimal?)null, false));
    }

    [Fact]
    public async Task A_catalog_sweep_never_overwrites_a_confirmed_match_url_or_confirmed_at()
    {
        var owner = await CleanAsync();
        var confirmed = await AddListingAsync(await ProductAsync(owner, "Mozzarella"), Store.Aldi, AldiUrl, confirmedAt: Day1.AddDays(-3));

        await RecordAsync(Day1, Reading(Store.Aldi, "000000000000900101", AldiUrl, price: 1.49m));

        var listing = await ListingAsync(confirmed);
        listing.ConfirmedAt.Should().Be(Day1.AddDays(-3));
        listing.Url.Should().Be(AldiUrl);
        (await ObservationsAsync(confirmed)).Should().ContainSingle();
    }
}
