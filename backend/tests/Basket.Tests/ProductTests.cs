using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

public sealed class ProductTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public ProductTests(BasketFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid ProductId)> WithProductAsync(string name)
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(db => db.Products.ExecuteDeleteAsync());
        var response = await owner.PostAsJsonAsync("/api/products", new { name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (owner, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> ProductAsync(HttpClient client, Guid id)
    {
        var all = await client.GetFromJsonAsync<JsonElement>("/api/products");
        return all.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
    }

    private static JsonElement Entry(JsonElement product, string store) =>
        product.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == store);

    private async Task<Guid> ListingIdAsync(Guid productId, Store store) =>
        await _factory.WithDb(db => db.StoreListings.Where(l => l.ProductId == productId && l.Store == store).Select(l => l.Id).SingleAsync());

    [Fact]
    public async Task A_new_product_starts_unlinked_in_all_three_shops_and_lidl_says_last_paid_is_not_connected()
    {
        var (owner, id) = await WithProductAsync("  Whole   milk 2L ");

        var product = await ProductAsync(owner, id);

        product.GetProperty("name").GetString().Should().Be("Whole milk 2L");
        product.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("store").GetString()).Should().Equal("lidl", "aldi", "tesco");
        foreach (var store in product.GetProperty("stores").EnumerateArray())
        {
            store.GetProperty("listing").ValueKind.Should().Be(JsonValueKind.Null);
            store.GetProperty("price").ValueKind.Should().Be(JsonValueKind.Null);
        }

        product.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("readBy").GetString()).Should().Equal("nightly", "pc", "pc");
        Entry(product, "lidl").GetProperty("lastPaid").GetProperty("state").GetString().Should().Be("not_connected");
        Entry(product, "aldi").GetProperty("lastPaid").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Blank_and_duplicate_names_are_refused()
    {
        var (owner, _) = await WithProductAsync("Butter");

        (await owner.PostAsJsonAsync("/api/products", new { name = "   " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/products", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/products", new { name = "butter" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Linking_a_shop_page_stores_the_tidied_address_and_relinking_forgets_the_old_readings()
    {
        var (owner, id) = await WithProductAsync("Chicken skewers");

        var linked = await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = "https://lidl.ie/p/vitasia-10-chicken-breast-skewers/p11115738?utm=x" });
        linked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()
            .Should().Be("https://www.lidl.ie/p/vitasia-10-chicken-breast-skewers/p11115738");

        var listingId = await ListingIdAsync(id, Store.Lidl);
        await _factory.WithDb(async db =>
        {
            db.PriceObservations.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listingId, ObservedOn = new DateOnly(2026, 11, 4), ObservedAt = DateTimeOffset.UtcNow, Price = 2.19m, InStock = true });
            await db.SaveChangesAsync();
            return 0;
        });

        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = "https://www.lidl.ie/p/vitasia-10-chicken-breast-skewers/p11115738" })).EnsureSuccessStatusCode();
        (await _factory.WithDb(db => db.PriceObservations.CountAsync(o => o.ListingId == listingId))).Should().Be(1, "the same page keeps its history");

        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = "https://www.lidl.ie/p/aubergine/p11214436" })).EnsureSuccessStatusCode();
        (await _factory.WithDb(db => db.PriceObservations.CountAsync(o => o.ListingId == listingId))).Should().Be(0, "another page is another product");
        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.ProductId == id))).Should().Be(1);
    }

    [Theory]
    [InlineData("lidl", "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541")]
    [InlineData("lidl", "http://www.lidl.ie/p/aubergine/p11214436")]
    [InlineData("aldi", "https://www.aldi.ie/results?q=milk")]
    [InlineData("tesco", "https://evil.example/groceries/en-IE/products/254317390")]
    [InlineData("tesco", "")]
    public async Task A_link_that_is_not_that_shops_product_page_is_refused_with_an_example(string store, string url)
    {
        var (owner, id) = await WithProductAsync("Anything");

        var response = await owner.PutAsJsonAsync($"/api/products/{id}/stores/{store}", new { url });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("url_invalid");
        body.GetProperty("message").GetString().Should().Contain("like https://");
    }

    [Fact]
    public async Task Unknown_shops_and_products_are_404()
    {
        var (owner, id) = await WithProductAsync("Anything");

        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/dunnes", new { url = "https://www.dunnesstoresgrocery.com/x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PutAsJsonAsync($"/api/products/{Guid.NewGuid()}/stores/lidl", new { url = "https://www.lidl.ie/p/aubergine/p11214436" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.DeleteAsync($"/api/products/{id}/stores/lidl")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.DeleteAsync($"/api/products/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_page_shows_the_latest_reading_per_shop_with_its_date_and_the_best_price()
    {
        var (owner, id) = await WithProductAsync("Chicken skewers");
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = "https://www.lidl.ie/p/vitasia-10-chicken-breast-skewers/p11115738" })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/aldi", new { url = "https://www.aldi.ie/product/actileaf-uht-coconut-milk-000000000000423109" })).EnsureSuccessStatusCode();
        var lidl = await ListingIdAsync(id, Store.Lidl);
        var aldi = await ListingIdAsync(id, Store.Aldi);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_factory.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin")).DateTime);
        await _factory.WithDb(async db =>
        {
            db.PriceObservations.AddRange(
                new PriceObservation { Id = Guid.NewGuid(), ListingId = lidl, ObservedOn = today.AddDays(-2), ObservedAt = DateTimeOffset.UtcNow, Price = 2.49m, InStock = true },
                new PriceObservation { Id = Guid.NewGuid(), ListingId = lidl, ObservedOn = today, ObservedAt = DateTimeOffset.UtcNow, Price = 2.29m, LoyaltyPrice = 1.99m, LoyaltyScheme = "Lidl Plus", PromoText = "Lidl Plus deal", ValidFrom = today.AddDays(-1), ValidUntil = today.AddDays(5), InStock = true },
                new PriceObservation { Id = Guid.NewGuid(), ListingId = aldi, ObservedOn = today, ObservedAt = DateTimeOffset.UtcNow, Price = 1.39m, WasPrice = 1.49m, PromoText = "Save 6%", InStock = true });
            await db.SaveChangesAsync();
            return 0;
        });

        var product = await ProductAsync(owner, id);

        var lidlPrice = Entry(product, "lidl").GetProperty("price");
        lidlPrice.GetProperty("observedOn").GetString().Should().Be(today.ToString("yyyy-MM-dd"));
        lidlPrice.GetProperty("state").GetString().Should().Be("current");
        lidlPrice.GetProperty("price").GetDecimal().Should().Be(2.29m);
        lidlPrice.GetProperty("loyaltyPrice").GetDecimal().Should().Be(1.99m);
        lidlPrice.GetProperty("best").GetDecimal().Should().Be(1.99m);
        Entry(product, "aldi").GetProperty("price").GetProperty("best").GetDecimal().Should().Be(1.39m);
        Entry(product, "aldi").GetProperty("price").GetProperty("wasPrice").GetDecimal().Should().Be(1.49m);
        Entry(product, "tesco").GetProperty("price").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_offer_that_has_not_started_or_has_ended_is_not_offered_as_the_current_price()
    {
        var (owner, id) = await WithProductAsync("Gyozas");
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = "https://www.lidl.ie/p/chicken-vegetable-gyozas/p11272692" })).EnsureSuccessStatusCode();
        var lidl = await ListingIdAsync(id, Store.Lidl);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_factory.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin")).DateTime);
        var upcoming = new PriceObservation { Id = Guid.NewGuid(), ListingId = lidl, ObservedOn = today, ObservedAt = DateTimeOffset.UtcNow, Price = 2.19m, ValidFrom = today.AddDays(7), ValidUntil = today.AddDays(30), InStock = true };
        await _factory.WithDb(async db => { db.PriceObservations.Add(upcoming); await db.SaveChangesAsync(); return 0; });

        var price = Entry(await ProductAsync(owner, id), "lidl").GetProperty("price");
        price.GetProperty("state").GetString().Should().Be("upcoming");
        price.GetProperty("best").ValueKind.Should().Be(JsonValueKind.Null);
        price.GetProperty("validFrom").GetString().Should().Be(today.AddDays(7).ToString("yyyy-MM-dd"));

        await _factory.WithDb(async db =>
        {
            var row = await db.PriceObservations.SingleAsync(o => o.Id == upcoming.Id);
            row.ValidFrom = today.AddDays(-30);
            row.ValidUntil = today.AddDays(-1);
            await db.SaveChangesAsync();
            return 0;
        });
        Entry(await ProductAsync(owner, id), "lidl").GetProperty("price").GetProperty("state").GetString().Should().Be("ended");
    }

    [Fact]
    public async Task The_last_run_of_every_shop_is_reported_so_a_blocked_shop_is_visible()
    {
        var (owner, _) = await WithProductAsync("Anything");
        await _factory.WithDb(async db =>
        {
            await db.PricePullRuns.ExecuteDeleteAsync();
            var now = DateTimeOffset.UtcNow;
            db.PricePullRuns.AddRange(
                new PricePullRun { Id = Guid.NewGuid(), Store = Store.Tesco, StartedAt = now.AddDays(-1), FinishedAt = now.AddDays(-1), Outcome = PullOutcome.Ok, Listings = 1, Read = 1 },
                new PricePullRun { Id = Guid.NewGuid(), Store = Store.Tesco, StartedAt = now, FinishedAt = now, Outcome = PullOutcome.Blocked, Listings = 2, Read = 0, Message = "www.tesco.ie answered HTTP 403." });
            await db.SaveChangesAsync();
            return 0;
        });

        var stores = (await owner.GetFromJsonAsync<JsonElement>("/api/products")).GetProperty("stores").EnumerateArray().ToDictionary(s => s.GetProperty("store").GetString()!);

        stores["tesco"].GetProperty("lastRun").GetProperty("outcome").GetString().Should().Be("blocked");
        stores["tesco"].GetProperty("lastRun").GetProperty("message").GetString().Should().Contain("403");
        stores["lidl"].GetProperty("lastRun").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Deleting_a_product_removes_its_links_and_history()
    {
        var (owner, id) = await WithProductAsync("Temporary");
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/aldi", new { url = "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541" })).EnsureSuccessStatusCode();
        var aldi = await ListingIdAsync(id, Store.Aldi);
        await _factory.WithDb(async db =>
        {
            db.PriceObservations.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = aldi, ObservedOn = new DateOnly(2026, 11, 4), ObservedAt = DateTimeOffset.UtcNow, Price = 1.49m, InStock = true });
            await db.SaveChangesAsync();
            return 0;
        });

        (await owner.DeleteAsync($"/api/products/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.ProductId == id))).Should().Be(0);
        (await _factory.WithDb(db => db.PriceObservations.CountAsync(o => o.ListingId == aldi))).Should().Be(0);
    }

    [Fact]
    public async Task Both_people_may_manage_the_products()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var invite = await owner.PostAsJsonAsync("/api/invites", new { label = "Her" });
        invite.EnsureSuccessStatusCode();
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString()!.Split('#')[1];
        var her = _factory.NewBrowser();
        await _factory.RegisterAsync(her, token, "Her");
        await _factory.WithDb(db => db.Products.ExecuteDeleteAsync());

        (await her.PostAsJsonAsync("/api/products", new { name = "Spinach" })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await owner.GetFromJsonAsync<JsonElement>("/api/products")).GetProperty("products").GetArrayLength().Should().Be(1);
    }
}
