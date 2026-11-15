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
/// <c>GET /api/catalog/{id}/equivalents</c>: the matcher over the other shops' catalog, top 3 per shop, with a unit price beside the
/// source's. Against Postgres, every product invented.
/// </summary>
public sealed class CatalogEquivalentsTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public CatalogEquivalentsTests(BasketFactory factory) => _factory = factory;

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
        decimal amount,
        string unit,
        int packs = 1,
        decimal? price = 1.00m,
        string? barcode = null,
        string? brand = null,
        Action<StoreProduct>? change = null,
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
            SizeAmount = amount > 0 ? amount : null,
            SizeUnit = amount > 0 ? unit : null,
            SizePacks = packs,
            Barcode = barcode,
            FirstSeenAt = _factory.Clock.GetUtcNow().AddDays(-30),
            LastSeenAt = _factory.Clock.GetUtcNow(),
        };
        change?.Invoke(product);
        await _factory.WithDb(async db =>
        {
            db.StoreProducts.Add(product);
            if (price is not null)
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

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/catalog/{id}/equivalents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Shop(JsonElement body, string slug) =>
        body.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == slug);

    private static string[] Names(JsonElement shop) =>
        shop.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("name").GetString()!).ToArray();

    private HttpClient Fleet(params string[] scopes)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Fleet.Mint(string.Join(' ', scopes)));
        return client;
    }

    [Fact]
    public async Task Answers_the_other_three_shops_in_the_fixed_order_with_unit_prices_beside_the_source()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Whole Milk 2 L", 2000, "ml", price: 2.00m);
        await AddAsync(Store.Aldi, "Whole Milk 2 L", 2000, "ml", price: 1.80m);
        await AddAsync(Store.Tesco, "Whole Milk 2 L", 2000, "ml", price: 2.40m);

        var body = await GetAsync(owner, source.Id);

        body.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        var product = body.GetProperty("product");
        product.GetProperty("item").GetProperty("id").GetGuid().Should().Be(source.Id);
        product.GetProperty("basis").ValueKind.Should().Be(JsonValueKind.Null);
        product.GetProperty("comparison").GetProperty("unit").GetString().Should().Be("l");
        product.GetProperty("comparison").GetProperty("unitPrice").GetDecimal().Should().Be(1.00m);
        body.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("store").GetString()).Should().Equal("aldi", "tesco", "dunnes");

        var aldi = Shop(body, "aldi").GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        aldi.GetProperty("basis").GetString().Should().Be("name_size");
        aldi.GetProperty("comparison").GetProperty("unitPrice").GetDecimal().Should().Be(0.90m);
        aldi.GetProperty("comparison").GetProperty("difference").GetDecimal().Should().Be(-0.10m);
        aldi.GetProperty("comparison").GetProperty("pack").GetString().Should().Be("2 l");
        Shop(body, "tesco").GetProperty("items")[0].GetProperty("comparison").GetProperty("difference").GetDecimal().Should().Be(0.20m);
        Shop(body, "dunnes").GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Returns_at_most_three_per_shop_best_score_first_and_never_the_sources_own_shop()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Baked Beans 400 g", 400, "g");
        await AddAsync(Store.Lidl, "Baked Beans 400 g", 400, "g");
        var exact = await AddAsync(Store.Aldi, "Baked Beans 400 g", 400, "g");
        await AddAsync(Store.Aldi, "Fancy Baked Beans Sauce 400 g", 400, "g");
        await AddAsync(Store.Aldi, "Baked Beans Tomato Sauce Sweet 400 g", 400, "g");
        await AddAsync(Store.Aldi, "Baked Beans Tomato Sauce Spicy Hot 400 g", 400, "g");

        var body = await GetAsync(owner, source.Id);

        body.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("store").GetString()).Should().NotContain("lidl");
        var aldi = Shop(body, "aldi");
        Names(aldi).Should().Equal("Baked Beans 400 g", "Fancy Baked Beans Sauce 400 g", "Baked Beans Tomato Sauce Sweet 400 g");
        aldi.GetProperty("items")[0].GetProperty("item").GetProperty("id").GetGuid().Should().Be(exact.Id);
        aldi.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("score").GetInt32()).Should().Equal(90, 70, 66);
    }

    [Fact]
    public async Task Rows_of_equal_score_put_a_comparable_price_first_then_the_cheaper_and_a_stale_row_last()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Baked Beans 400 g", 400, "g");
        await AddAsync(Store.Aldi, "Baked Beans Alpha 400 g", 400, "g", price: 2.00m);
        await AddAsync(Store.Aldi, "Baked Beans Beta 400 g", 400, "g", price: 1.00m, change: p => p.LastSeenAt = _factory.Clock.GetUtcNow().AddDays(-5));
        await AddAsync(Store.Aldi, "Baked Beans Gamma 400 g", 400, "g", price: 1.50m);

        var body = await GetAsync(owner, source.Id);

        var aldi = Shop(body, "aldi");
        aldi.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("score").GetInt32()).Distinct().Should().ContainSingle();
        Names(aldi).Should().Equal("Baked Beans Gamma 400 g", "Baked Beans Alpha 400 g", "Baked Beans Beta 400 g");
    }

    [Fact]
    public async Task A_brand_kept_in_its_own_field_does_not_hide_the_same_product_in_another_shop()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Baked Beans", 415, "g", brand: "Heinz");
        await AddAsync(Store.Aldi, "Baked Beans", 415, "g", brand: "Heinz");
        await AddAsync(Store.Tesco, "Baked Beans", 415, "g", brand: "Branston");

        var body = await GetAsync(owner, source.Id);

        var aldi = Shop(body, "aldi").GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        aldi.GetProperty("item").GetProperty("name").GetString().Should().Be("Baked Beans");
        aldi.GetProperty("basis").GetString().Should().Be("name_size");
        Shop(body, "tesco").GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_brand_written_in_the_name_finds_the_same_product_in_another_shop()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Heinz Baked Beans", 415, "g", brand: "Heinz");
        await AddAsync(Store.Aldi, "Heinz Baked Beans", 415, "g");
        await AddAsync(Store.Dunnes, "Branston Baked Beans", 415, "g");

        var body = await GetAsync(owner, source.Id);

        Names(Shop(body, "aldi")).Should().Equal("Heinz Baked Beans");
        Shop(body, "dunnes").GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_different_size_or_pack_is_never_an_equivalent()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Cola 330 ml", 330, "ml", packs: 6);
        await AddAsync(Store.Aldi, "Cola 330 ml", 330, "ml");
        await AddAsync(Store.Tesco, "Cola 500 ml", 500, "ml", packs: 6);
        var same = await AddAsync(Store.Dunnes, "Cola 330 ml", 330, "ml", packs: 6);

        var body = await GetAsync(owner, source.Id);

        Shop(body, "aldi").GetProperty("items").GetArrayLength().Should().Be(0);
        Shop(body, "tesco").GetProperty("items").GetArrayLength().Should().Be(0);
        Shop(body, "dunnes").GetProperty("items")[0].GetProperty("item").GetProperty("id").GetGuid().Should().Be(same.Id);
    }

    [Fact]
    public async Task A_multipack_is_compared_per_litre_over_all_its_packs()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Cola 330 ml", 330, "ml", packs: 6, price: 3.96m);
        await AddAsync(Store.Aldi, "Cola 330 ml", 330, "ml", packs: 6, price: 5.94m);

        var body = await GetAsync(owner, source.Id);

        body.GetProperty("product").GetProperty("comparison").GetProperty("unitPrice").GetDecimal().Should().Be(2.00m);
        Shop(body, "aldi").GetProperty("items")[0].GetProperty("comparison").GetProperty("unitPrice").GetDecimal().Should().Be(3.00m);
    }

    [Fact]
    public async Task The_same_barcode_matches_whatever_the_names_say_and_different_barcodes_never_do()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Jamon Serrano 100 g", 100, "g", barcode: "05012345678900");
        var same = await AddAsync(Store.Aldi, "Spanish Cured Ham Slices", 0, "g", barcode: "05012345678900");
        await AddAsync(Store.Tesco, "Jamon Serrano 100 g", 100, "g", barcode: "04006381333931");

        var body = await GetAsync(owner, source.Id);

        var aldi = Shop(body, "aldi").GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        aldi.GetProperty("item").GetProperty("id").GetGuid().Should().Be(same.Id);
        aldi.GetProperty("basis").GetString().Should().Be("barcode");
        aldi.GetProperty("score").GetInt32().Should().Be(100);
        aldi.GetProperty("comparison").GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Contain("no_size");
        Shop(body, "tesco").GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_stale_out_of_stock_or_unpriced_row_is_flagged_and_has_no_difference()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Plain Flour 1 kg", 1000, "g", price: 1.00m);
        var old = await AddAsync(Store.Aldi, "Plain Flour 1 kg", 1000, "g", price: 0.80m, change: p => p.LastSeenAt = _factory.Clock.GetUtcNow().AddDays(-5));
        var gone = await AddAsync(Store.Tesco, "Plain Flour 1 kg", 1000, "g", price: 0.80m, priced: r => r.InStock = false);
        var none = await AddAsync(Store.Dunnes, "Plain Flour 1 kg", 1000, "g", price: null);

        var body = await GetAsync(owner, source.Id);

        string[] Flags(string shop) =>
            Shop(body, shop).GetProperty("items")[0].GetProperty("comparison").GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Flags("aldi").Should().Contain("stale");
        Flags("tesco").Should().Contain("out_of_stock");
        Flags("dunnes").Should().Contain("no_price");
        foreach (var shop in new[] { "aldi", "tesco", "dunnes" })
        {
            Shop(body, shop).GetProperty("items")[0].GetProperty("comparison").GetProperty("difference").ValueKind.Should().Be(JsonValueKind.Null);
        }

        (old.Id, gone.Id, none.Id).Should().NotBe((Guid.Empty, Guid.Empty, Guid.Empty));
    }

    [Fact]
    public async Task A_discontinued_row_is_not_offered_but_a_discontinued_source_is_answered()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Plain Flour 1 kg", 1000, "g", change: p => p.DiscontinuedAt = _factory.Clock.GetUtcNow());
        await AddAsync(Store.Aldi, "Plain Flour 1 kg", 1000, "g", change: p => p.DiscontinuedAt = _factory.Clock.GetUtcNow());
        var live = await AddAsync(Store.Tesco, "Plain Flour 1 kg", 1000, "g");

        var body = await GetAsync(owner, source.Id);

        Shop(body, "aldi").GetProperty("items").GetArrayLength().Should().Be(0);
        Shop(body, "tesco").GetProperty("items")[0].GetProperty("item").GetProperty("id").GetGuid().Should().Be(live.Id);
    }

    [Fact]
    public async Task A_product_with_no_size_and_no_barcode_says_why_nothing_was_looked_up()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Mystery Box", 0, "g");
        await AddAsync(Store.Aldi, "Mystery Box", 0, "g");

        var body = await GetAsync(owner, source.Id);

        body.GetProperty("reason").GetString().Should().Be("no_size_or_barcode");
        body.GetProperty("stores").EnumerateArray().Should().OnlyContain(s => s.GetProperty("items").GetArrayLength() == 0);
    }

    [Fact]
    public async Task Nothing_is_written_and_an_unknown_id_is_404()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Whole Milk 2 L", 2000, "ml");
        await AddAsync(Store.Aldi, "Whole Milk 2 L", 2000, "ml");

        await GetAsync(owner, source.Id);
        await GetAsync(owner, source.Id);

        var (candidates, listings, products) = await _factory.WithDb(async db =>
            (await db.MatchCandidates.CountAsync(), await db.StoreListings.CountAsync(), await db.StoreProducts.CountAsync()));
        candidates.Should().Be(0);
        products.Should().Be(2);
        listings.Should().Be(0);
        (await owner.GetAsync($"/api/catalog/{Guid.NewGuid()}/equivalents")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Who_may_read_follows_the_catalog_policy()
    {
        var owner = await SeedAsync();
        var source = await AddAsync(Store.Lidl, "Whole Milk 2 L", 2000, "ml");
        var (member, _) = await _factory.MemberAsync(owner);

        (await member.GetAsync($"/api/catalog/{source.Id}/equivalents")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Fleet(FleetScopes.CatalogRead).GetAsync($"/api/catalog/{source.Id}/equivalents")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Fleet(FleetScopes.PriceIngest).GetAsync($"/api/catalog/{source.Id}/equivalents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Fleet(FleetScopes.CatalogAdd).GetAsync($"/api/catalog/{source.Id}/equivalents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
