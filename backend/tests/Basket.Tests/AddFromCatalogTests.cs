using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// Adding a shelf product by tapping a catalog row: <c>POST /api/products</c> with a <c>catalogId</c>. The product is named from the row,
/// takes its barcode, the row's page and photo become its page in that shop, and the server's candidate search then looks at the other
/// shops. Synthetic products and ids only.
/// </summary>
public sealed class AddFromCatalogTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public AddFromCatalogTests(BasketFactory factory) => _factory = factory;

    private static string Ean13(string twelve)
    {
        var sum = twelve.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + ((10 - (sum % 10)) % 10);
    }

    private static string Url(Store store, string id) => store switch
    {
        Store.Tesco => $"https://www.tesco.ie/groceries/en-IE/products/{id}",
        Store.Aldi => $"https://www.aldi.ie/product/item-{id}",
        Store.Lidl => $"https://www.lidl.ie/p/item/p{id}",
        _ => $"https://www.dunnesstoresgrocery.com/product/item-{id}",
    };

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

    private async Task<Guid> RowAsync(
        Store store,
        string id,
        string name,
        string? brand = null,
        string? size = null,
        string? barcode = null,
        string? category = null,
        decimal? price = 1.49m,
        string? image = "https://images.example.test/p.jpg")
    {
        var item = new CatalogItem(id, Url(store, id), name, brand, size, Barcode: barcode, Category: category, ImageUrl: image, Price: price);
        CatalogReading.TryCreate(store, item, out var reading, out var reason).Should().BeTrue(reason);
        return await _factory.WithDb(async db =>
        {
            await new CatalogRecorder(db, new PriceRecorder(db)).RecordAsync(store, [reading!], _factory.Clock.GetUtcNow(), CancellationToken.None);
            await db.SaveChangesAsync();
            return await db.StoreProducts.Where(p => p.Store == store && p.ExternalId == reading!.ExternalId).Select(p => p.Id).SingleAsync();
        });
    }

    private HttpClient Fleet()
    {
        var fleet = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        fleet.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Fleet.Mint("fleet:read basket:catalog-add"));
        return fleet;
    }

    private static Task<HttpResponseMessage> AddAsync(HttpClient client, Guid catalogId, string? name = null) =>
        client.PostAsJsonAsync("/api/products", new { catalogId, name });

    private static async Task<JsonElement> CreatedBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body.ToString());
        return body;
    }

    private static async Task<Guid> CreatedAsync(HttpResponseMessage response) => (await CreatedBodyAsync(response)).GetProperty("id").GetGuid();

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [Fact]
    public async Task Tapping_a_row_makes_the_product_from_it_and_links_that_shops_page()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000100");
        var row = await RowAsync(Store.Lidl, "10020001", "Whole Milk", "Dairy Farm", "2 L", code, "Chilled Food > Milk", 1.19m);

        var response = await AddAsync(owner, row);

        var body = await CreatedBodyAsync(response);
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("name").GetString().Should().Be("Dairy Farm Whole Milk");
        body.GetProperty("barcode").GetString().Should().Be(code.PadLeft(14, '0'));
        var lidl = body.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "lidl");
        lidl.GetProperty("listing").GetProperty("url").GetString().Should().Be(Url(Store.Lidl, "10020001"));
        lidl.GetProperty("listing").GetProperty("confirmed").GetBoolean().Should().BeTrue();

        await _factory.WithDb(async db =>
        {
            var listing = await db.StoreListings.AsNoTracking().SingleAsync(l => l.ProductId == id && l.Store == Store.Lidl);
            listing.StoreProductId.Should().Be(row);
            listing.Title.Should().Be("Whole Milk");
            listing.ImageUrl.Should().Be("https://images.example.test/p.jpg");
            listing.Category.Should().Be("Chilled Food > Milk");
            listing.ConfirmedAt.Should().NotBeNull();
            (await db.Products.AsNoTracking().SingleAsync(p => p.Id == id)).AddedByUserId.Should().NotBeEmpty();
            var observation = await db.PriceObservations.AsNoTracking().SingleAsync(o => o.ListingId == listing.Id);
            observation.Price.Should().Be(1.19m);
            return 0;
        });
    }

    [Fact]
    public async Task The_servers_candidate_search_then_fills_the_other_shops()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000117");
        var row = await RowAsync(Store.Lidl, "10020002", "Whole Milk", null, "2 L", code);
        await RowAsync(Store.Aldi, "000000000000910001", "Mystery Dairy Drink", null, "Pack", code);
        await RowAsync(Store.Tesco, "254400011", "Fresh Whole Milk", null, "2 L");
        await RowAsync(Store.Tesco, "254400012", "Whole Milk", null, "1 L");

        var id = await CreatedAsync(await AddAsync(owner, row));

        await _factory.WithDb(async db =>
        {
            var listings = (await db.StoreListings.AsNoTracking().Where(l => l.ProductId == id).ToListAsync()).OrderBy(l => l.Store).ToList();
            listings.Select(l => (l.Store, Confirmed: l.ConfirmedAt != null)).Should().Equal((Store.Lidl, true), (Store.Aldi, false));
            var proposals = await db.MatchCandidates.AsNoTracking().Where(c => c.ProductId == id && c.Status == CandidateStatus.Proposed).ToListAsync();
            proposals.Should().ContainSingle().Which.Should().Match<MatchCandidate>(c => c.Store == Store.Tesco && c.Url == Url(Store.Tesco, "254400011"));
            return 0;
        });
    }

    [Fact]
    public async Task A_brand_the_name_already_carries_is_not_said_twice()
    {
        var owner = await CleanAsync();
        var row = await RowAsync(Store.Lidl, "10020003", "Milbona Greek Yoghurt", "Milbona", "500 g");

        var response = await AddAsync(owner, row);

        (await CreatedBodyAsync(response)).GetProperty("name").GetString().Should().Be("Milbona Greek Yoghurt");
    }

    [Fact]
    public async Task A_name_in_the_request_wins_over_the_rows()
    {
        var owner = await CleanAsync();
        var row = await RowAsync(Store.Lidl, "10020004", "Whole Milk", null, "2 L");

        var response = await AddAsync(owner, row, "The good milk");

        (await CreatedBodyAsync(response)).GetProperty("name").GetString().Should().Be("The good milk");
    }

    [Fact]
    public async Task A_row_with_no_barcode_and_no_price_still_makes_a_product_with_its_page()
    {
        var owner = await CleanAsync();
        var row = await RowAsync(Store.Tesco, "254400021", "Oat Drink", null, "1 L", barcode: null, price: null, image: null);

        var id = await CreatedAsync(await AddAsync(owner, row));

        await _factory.WithDb(async db =>
        {
            (await db.Products.AsNoTracking().SingleAsync(p => p.Id == id)).Barcode.Should().BeNull();
            var listing = await db.StoreListings.AsNoTracking().SingleAsync(l => l.ProductId == id);
            listing.Store.Should().Be(Store.Tesco);
            listing.ImageUrl.Should().BeNull();
            (await db.PriceObservations.AnyAsync(o => o.ListingId == listing.Id)).Should().BeFalse("a row with nothing to say is not a reading");
            return 0;
        });
    }

    [Fact]
    public async Task A_second_tap_on_a_page_already_on_the_shelf_is_refused_and_adds_nothing()
    {
        var owner = await CleanAsync();
        var row = await RowAsync(Store.Lidl, "10020005", "Whole Milk", null, "2 L");
        await CreatedAsync(await AddAsync(owner, row));

        var again = await AddAsync(owner, row, "Milk, again");

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(again)).Should().Be("product_exists");
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_name_already_on_the_shelf_is_refused()
    {
        var owner = await CleanAsync();
        await CreatedAsync(await owner.PostAsJsonAsync("/api/products", new { name = "Whole Milk" }));
        var row = await RowAsync(Store.Lidl, "10020006", "Whole Milk", null, "2 L");

        var response = await AddAsync(owner, row);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(response)).Should().Be("product_exists");
        (await _factory.WithDb(db => db.StoreListings.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_barcode_another_product_has_is_refused()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000124");
        await CreatedAsync(await owner.PostAsJsonAsync("/api/products", new { name = "Mine", barcode = code }));
        var row = await RowAsync(Store.Lidl, "10020007", "Whole Milk", null, "2 L", code);

        var response = await AddAsync(owner, row);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(response)).Should().Be("barcode_exists");
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_row_that_is_not_in_the_catalog_or_is_no_longer_listed_is_refused()
    {
        var owner = await CleanAsync();
        var gone = await RowAsync(Store.Lidl, "10020008", "Old Biscuits", null, "300 g");
        await _factory.WithDb(db => db.StoreProducts.Where(p => p.Id == gone).ExecuteUpdateAsync(s => s.SetProperty(p => p.DiscontinuedAt, DateTimeOffset.UtcNow)));

        var missing = await AddAsync(owner, Guid.NewGuid());
        var discontinued = await AddAsync(owner, gone);

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        discontinued.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(discontinued)).Should().Be("catalog_discontinued");
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_fleet_can_add_from_the_catalog_and_its_page_stays_unconfirmed()
    {
        await CleanAsync();
        var row = await RowAsync(Store.Aldi, "000000000000910002", "Rolled Oats", null, "1 kg");

        var id = await CreatedAsync(await AddAsync(Fleet(), row));

        await _factory.WithDb(async db =>
        {
            var listing = await db.StoreListings.AsNoTracking().SingleAsync(l => l.ProductId == id);
            listing.StoreProductId.Should().Be(row);
            listing.ConfirmedAt.Should().BeNull("only a person confirms a match");
            return 0;
        });
    }
}
