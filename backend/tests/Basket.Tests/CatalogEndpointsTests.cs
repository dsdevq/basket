using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// Reading the combined catalog: <c>GET /api/catalog/search</c> and <c>GET /api/catalog/{id}</c>, against Postgres so the generated
/// <c>tsvector</c>, its ranking and its index are what runs. Every product here is invented.
/// </summary>
public sealed class CatalogEndpointsTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public CatalogEndpointsTests(BasketFactory factory) => _factory = factory;

    private static readonly DateTimeOffset Seen = DateTimeOffset.Parse("2026-11-14T03:00:00Z");

    private DateOnly Today => PriceRecorder.DublinDay(_factory.Clock.GetUtcNow());

    private async Task<HttpClient> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            return 0;
        });
        return owner;
    }

    private async Task<StoreProduct> AddAsync(
        Store store,
        string name,
        string? brand = null,
        Action<StoreProduct>? change = null,
        decimal? price = 1.00m,
        Action<StoreProductPrice>? priced = null)
    {
        var product = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Store = store,
            ExternalId = Guid.NewGuid().ToString("N")[..12],
            Url = $"https://{store.Host()}/p/{Guid.NewGuid():N}",
            Name = name,
            Brand = brand,
            FirstSeenAt = Seen.AddDays(-30),
            LastSeenAt = Seen,
        };
        change?.Invoke(product);
        await _factory.WithDb(async db =>
        {
            db.StoreProducts.Add(product);
            if (price is not null || priced is not null)
            {
                var row = new StoreProductPrice { StoreProductId = product.Id, ObservedOn = Today, Price = price, InStock = true };
                priced?.Invoke(row);
                db.StoreProductPrices.Add(row);
            }

            await db.SaveChangesAsync();
            return 0;
        });
        return product;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Names(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();

    private HttpClient Fleet(params string[] scopes)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Fleet.Mint(string.Join(' ', scopes)));
        return client;
    }

    // ---- search -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_answers_what_a_tile_shows_for_each_product()
    {
        var owner = await SeedAsync();
        var product = await AddAsync(
            Store.Lidl,
            "Dark Chocolate Bar",
            "Cocoa Farm",
            p =>
            {
                p.SizeText = "100 g";
                p.UnitPrice = 14.90m;
                p.UnitPriceBasis = "1 KG";
                p.ImageUrl = "https://images.example.test/dark-bar.jpg";
            },
            price: 1.49m);

        var response = await owner.GetAsync("/api/catalog/search?q=chocolate");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("query").GetString().Should().Be("chocolate");
        body.GetProperty("store").ValueKind.Should().Be(JsonValueKind.Null);
        var item = body.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("id").GetGuid().Should().Be(product.Id);
        item.GetProperty("store").GetString().Should().Be("lidl");
        item.GetProperty("storeName").GetString().Should().Be("Lidl");
        item.GetProperty("name").GetString().Should().Be("Dark Chocolate Bar");
        item.GetProperty("brand").GetString().Should().Be("Cocoa Farm");
        item.GetProperty("sizeText").GetString().Should().Be("100 g");
        item.GetProperty("unitPrice").GetDecimal().Should().Be(14.90m);
        item.GetProperty("unitPriceBasis").GetString().Should().Be("1 KG");
        item.GetProperty("imageUrl").GetString().Should().Be("https://images.example.test/dark-bar.jpg");
        item.GetProperty("url").GetString().Should().Be(product.Url);
        item.GetProperty("lastSeenAt").GetDateTimeOffset().Should().Be(Seen);
        var price = item.GetProperty("price");
        price.GetProperty("observedOn").GetString().Should().Be(Today.ToString("yyyy-MM-dd"));
        price.GetProperty("state").GetString().Should().Be("current");
        price.GetProperty("price").GetDecimal().Should().Be(1.49m);
        price.GetProperty("best").GetDecimal().Should().Be(1.49m);
    }

    [Fact]
    public async Task Search_finds_a_brand_as_well_as_a_name()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Aldi, "Crunchy Oat Biscuits", "Mill Lane");
        await AddAsync(Store.Aldi, "Plain Flour", "Other Brand");

        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=mill"))).Should().Equal("Crunchy Oat Biscuits");
    }

    [Fact]
    public async Task Search_matches_every_word_as_a_prefix_so_a_half_typed_query_still_finds_things()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Lidl, "Milk Chocolate Biscuits");
        await AddAsync(Store.Tesco, "Chocolate Milk Drink");
        await AddAsync(Store.Dunnes, "Dark Chocolate Bar");
        await AddAsync(Store.Lidl, "Whole Milk");

        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=choc"))).Should().BeEquivalentTo("Milk Chocolate Biscuits", "Chocolate Milk Drink", "Dark Chocolate Bar");
        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=CHOC%20milk"))).Should().BeEquivalentTo("Milk Chocolate Biscuits", "Chocolate Milk Drink");
    }

    [Fact]
    public async Task Search_stems_words_the_way_the_index_does()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Tesco, "Plum Tomatoes");

        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=tomato"))).Should().Equal("Plum Tomatoes");
        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=tomatoes"))).Should().Equal("Plum Tomatoes");
    }

    [Fact]
    public async Task Search_ranks_the_better_match_first_and_breaks_ties_by_name()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Lidl, "Almond Chocolate Bar");
        await AddAsync(Store.Aldi, "Zesty Chocolate Chocolate Cake");
        await AddAsync(Store.Tesco, "Berry Chocolate Bar");

        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=chocolate")))
            .Should().Equal("Zesty Chocolate Chocolate Cake", "Almond Chocolate Bar", "Berry Chocolate Bar");
    }

    [Fact]
    public async Task Search_can_be_limited_to_one_shop_in_any_case()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Lidl, "Sparkling Water");
        await AddAsync(Store.Aldi, "Sparkling Water Lemon");
        await AddAsync(Store.Aldi, "Sparkling Water Lime");

        var body = await JsonAsync(await owner.GetAsync("/api/catalog/search?q=sparkling&store=ALDI"));

        body.GetProperty("store").GetString().Should().Be("aldi");
        Names(body).Should().Equal("Sparkling Water Lemon", "Sparkling Water Lime");
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("store").GetString()).Should().OnlyContain(s => s == "aldi");
    }

    [Fact]
    public async Task Search_refuses_a_shop_basket_does_not_read()
    {
        var owner = await SeedAsync();

        var response = await owner.GetAsync("/api/catalog/search?q=water&store=supervalu");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("unknown_store");
    }

    [Fact]
    public async Task Search_returns_at_most_fifty_and_a_smaller_limit_is_honoured()
    {
        var owner = await SeedAsync();
        await _factory.WithDb(async db =>
        {
            for (var i = 0; i < 60; i++)
            {
                db.StoreProducts.Add(new StoreProduct
                {
                    Id = Guid.NewGuid(),
                    Store = Store.Aldi,
                    ExternalId = $"sw{i}",
                    Url = $"https://www.aldi.ie/product/sparkling-water-{i}",
                    Name = $"Sparkling Water {i:00}",
                    FirstSeenAt = Seen,
                    LastSeenAt = Seen,
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });

        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=sparkling"))).Should().HaveCount(50);
        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=sparkling&limit=500"))).Should().HaveCount(50);
        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=sparkling&limit=5"))).Should().Equal(
            "Sparkling Water 00", "Sparkling Water 01", "Sparkling Water 02", "Sparkling Water 03", "Sparkling Water 04");
        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=sparkling&limit=0"))).Should().HaveCount(1);
    }

    [Fact]
    public async Task Search_leaves_out_what_the_shops_no_longer_list()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Dunnes, "Digestive Biscuits", change: p => p.DiscontinuedAt = Seen);
        await AddAsync(Store.Dunnes, "Oat Biscuits");

        Names(await JsonAsync(await owner.GetAsync("/api/catalog/search?q=biscuits"))).Should().Equal("Oat Biscuits");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("  ")]
    [InlineData("&:*")]
    [InlineData("!!")]
    public async Task Search_needs_a_couple_of_letters(string q)
    {
        var owner = await SeedAsync();

        var response = await owner.GetAsync($"/api/catalog/search?q={Uri.EscapeDataString(q)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("query_too_short");
    }

    [Fact]
    public async Task Search_without_a_query_is_refused_too()
    {
        var owner = await SeedAsync();

        (await owner.GetAsync("/api/catalog/search")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("choc' | 'x")]
    [InlineData("choc:* & !(milk")]
    [InlineData("choc\\); DROP TABLE store_product; --")]
    public async Task Search_text_is_never_query_syntax(string q)
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Lidl, "Dark Chocolate Bar");

        var response = await owner.GetAsync($"/api/catalog/search?q={Uri.EscapeDataString(q)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Search_with_nothing_found_is_an_empty_list_not_an_error()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Lidl, "Whole Milk");

        var response = await owner.GetAsync("/api/catalog/search?q=xylophone");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Names(await JsonAsync(response)).Should().BeEmpty();
    }

    // ---- the price a result carries -----------------------------------------------------------------------------

    [Fact]
    public async Task A_result_carries_the_latest_price_with_its_promo_and_loyalty_price()
    {
        var owner = await SeedAsync();
        var product = await AddAsync(
            Store.Tesco,
            "Chocolate Milk Drink",
            price: 2.50m,
            priced: p =>
            {
                p.WasPrice = 3.00m;
                p.LoyaltyPrice = 1.99m;
                p.LoyaltyScheme = "Clubcard";
                p.PromoText = "Clubcard Price";
                p.ValidUntil = Today.AddDays(3);
            });
        await _factory.WithDb(async db =>
        {
            // An older reading of the same product: the latest one is what a result shows.
            db.StoreProductPrices.Add(new StoreProductPrice { StoreProductId = product.Id, ObservedOn = Today.AddDays(-20), Price = 3.10m, InStock = true });
            await db.SaveChangesAsync();
            return 0;
        });

        var item = (await JsonAsync(await owner.GetAsync("/api/catalog/search?q=chocolate"))).GetProperty("items").EnumerateArray().Single();

        var price = item.GetProperty("price");
        price.GetProperty("state").GetString().Should().Be("current");
        price.GetProperty("price").GetDecimal().Should().Be(2.50m);
        price.GetProperty("wasPrice").GetDecimal().Should().Be(3.00m);
        price.GetProperty("loyaltyPrice").GetDecimal().Should().Be(1.99m);
        price.GetProperty("loyaltyScheme").GetString().Should().Be("Clubcard");
        price.GetProperty("promoText").GetString().Should().Be("Clubcard Price");
        price.GetProperty("validUntil").GetString().Should().Be(Today.AddDays(3).ToString("yyyy-MM-dd"));
        price.GetProperty("best").GetDecimal().Should().Be(1.99m);
        price.GetProperty("inStock").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_price_is_dated_and_says_whether_it_can_be_used_today()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Aldi, "Cereal Bar Upcoming", priced: p => p.ValidFrom = Today.AddDays(2));
        await AddAsync(Store.Aldi, "Cereal Bar Ended", priced: p => p.ValidUntil = Today.AddDays(-1));
        await AddAsync(Store.Aldi, "Cereal Bar Unlisted", price: null, priced: _ => { });

        var items = (await JsonAsync(await owner.GetAsync("/api/catalog/search?q=cereal"))).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("name").GetString()!, i => i.GetProperty("price"));

        items["Cereal Bar Upcoming"].GetProperty("state").GetString().Should().Be("upcoming");
        items["Cereal Bar Ended"].GetProperty("state").GetString().Should().Be("ended");
        items["Cereal Bar Unlisted"].GetProperty("state").GetString().Should().Be("unavailable");
        items["Cereal Bar Upcoming"].GetProperty("best").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_product_with_no_price_yet_is_still_found_with_a_null_price()
    {
        var owner = await SeedAsync();
        await AddAsync(Store.Lidl, "Garden Spade", price: null);

        var item = (await JsonAsync(await owner.GetAsync("/api/catalog/search?q=spade"))).GetProperty("items").EnumerateArray().Single();

        item.GetProperty("price").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- detail -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Detail_answers_the_whole_product()
    {
        var owner = await SeedAsync();
        var product = await AddAsync(
            Store.Lidl,
            "Sparkling Water Lemon",
            "Spring Co",
            p =>
            {
                p.SizeText = "6 x 330ml";
                p.SizeAmount = 330m;
                p.SizeUnit = StoreProduct.Millilitres;
                p.SizePacks = 6;
                p.UnitPrice = 0.50m;
                p.UnitPriceBasis = "1 L";
                p.Barcode = "05012345678900";
                p.Category = "Drinks > Water";
                p.ImageUrl = "https://images.example.test/water.jpg";
            },
            price: 0.99m);

        var response = await owner.GetAsync($"/api/catalog/{product.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("id").GetGuid().Should().Be(product.Id);
        body.GetProperty("store").GetString().Should().Be("lidl");
        body.GetProperty("storeName").GetString().Should().Be("Lidl");
        body.GetProperty("name").GetString().Should().Be("Sparkling Water Lemon");
        body.GetProperty("brand").GetString().Should().Be("Spring Co");
        body.GetProperty("sizeText").GetString().Should().Be("6 x 330ml");
        body.GetProperty("sizeAmount").GetDecimal().Should().Be(330m);
        body.GetProperty("sizeUnit").GetString().Should().Be("ml");
        body.GetProperty("sizePacks").GetInt32().Should().Be(6);
        body.GetProperty("unitPrice").GetDecimal().Should().Be(0.50m);
        body.GetProperty("unitPriceBasis").GetString().Should().Be("1 L");
        body.GetProperty("barcode").GetString().Should().Be("05012345678900");
        body.GetProperty("category").GetString().Should().Be("Drinks > Water");
        body.GetProperty("imageUrl").GetString().Should().Be("https://images.example.test/water.jpg");
        body.GetProperty("url").GetString().Should().Be(product.Url);
        body.GetProperty("firstSeenAt").GetDateTimeOffset().Should().Be(Seen.AddDays(-30));
        body.GetProperty("lastSeenAt").GetDateTimeOffset().Should().Be(Seen);
        body.GetProperty("discontinuedAt").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("price").GetProperty("price").GetDecimal().Should().Be(0.99m);
    }

    [Fact]
    public async Task Detail_still_answers_a_product_the_shops_stopped_listing()
    {
        var owner = await SeedAsync();
        var product = await AddAsync(Store.Dunnes, "Digestive Biscuits", change: p => p.DiscontinuedAt = Seen);

        var response = await owner.GetAsync($"/api/catalog/{product.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("discontinuedAt").GetDateTimeOffset().Should().Be(Seen);
    }

    [Fact]
    public async Task Detail_of_an_unknown_product_is_404_and_so_is_a_malformed_id()
    {
        var owner = await SeedAsync();

        (await owner.GetAsync($"/api/catalog/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync("/api/catalog/not-an-id")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- who may read -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_reads_the_catalog_like_the_owner()
    {
        var owner = await SeedAsync();
        var product = await AddAsync(Store.Lidl, "Whole Milk");
        var (member, _) = await _factory.MemberAsync(owner);

        (await member.GetAsync("/api/catalog/search?q=milk")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.GetAsync($"/api/catalog/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_fleet_token_with_the_catalog_read_scope_reads_search_and_detail()
    {
        await SeedAsync();
        var product = await AddAsync(Store.Lidl, "Whole Milk");
        var fleet = Fleet(FleetScopes.PriceIngest, FleetScopes.CatalogRead);

        var search = await fleet.GetAsync("/api/catalog/search?q=milk");
        var detail = await fleet.GetAsync($"/api/catalog/{product.Id}");

        search.StatusCode.Should().Be(HttpStatusCode.OK);
        Names(await JsonAsync(search)).Should().Equal("Whole Milk");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(FleetScopes.PriceIngest)]
    [InlineData(FleetScopes.CatalogAdd)]
    [InlineData("fleet:read")]
    public async Task A_fleet_token_without_the_catalog_read_scope_is_forbidden(string scope)
    {
        await SeedAsync();
        var product = await AddAsync(Store.Lidl, "Whole Milk");
        var fleet = Fleet(scope);

        (await fleet.GetAsync("/api/catalog/search?q=milk")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await fleet.GetAsync($"/api/catalog/{product.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_catalog_routes_only_read()
    {
        var owner = await SeedAsync();
        var product = await AddAsync(Store.Lidl, "Whole Milk");

        (await owner.PostAsJsonAsync("/api/catalog/search", new { })).StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await owner.DeleteAsync($"/api/catalog/{product.Id}")).StatusCode.Should().NotBe(HttpStatusCode.NoContent);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(1);
    }
}
