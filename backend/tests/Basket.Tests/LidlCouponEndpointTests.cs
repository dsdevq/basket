using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.LidlPlus;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Basket.Tests.Support.FakeLidlPlusCoupons;

namespace Basket.Tests;

/// <summary>Lidl Plus coupons end to end: listing, switching on, failing quietly, and an active coupon changing a price. Lidl is a script; no test calls it.</summary>
public sealed class LidlCouponEndpointTests
{
    private const string Token = "A1B2C3D4E5F60718293A4B5C6D7E8F90-1";

    private static async Task<HttpClient> ConnectedOwnerAsync(BasketFactory factory)
    {
        var (owner, _) = await factory.OwnerAsync();
        (await owner.PutAsJsonAsync("/api/lidl-plus/connection", new { refreshToken = Token })).StatusCode.Should().Be(HttpStatusCode.Created);
        factory.Coupons.Reset();
        await factory.WithDb(async db => { await db.LidlCoupons.ExecuteDeleteAsync(); return 0; });
        return owner;
    }

    private static DateOnly Today(BasketFactory factory) => PriceRecorder.DublinDay(factory.Clock.GetUtcNow());

    private static JsonElement[] Coupons(JsonElement body) => body.GetProperty("coupons").EnumerateArray().ToArray();

    [Fact]
    public async Task The_list_shows_the_coupons_Lidl_gives_and_which_of_them_count_today()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        var today = Today(factory);
        factory.Coupons
            .Add(Coupon("C-1", "Chicken fillets", "20% off", until: today.AddDays(3)))
            .Add(Coupon("C-2", "Oat biscuits", "€1 off", until: today.AddDays(5), activated: true))
            .Add(Coupon("C-3", "Old deal", "10% off", until: today.AddDays(-1), activated: true));

        var body = await owner.GetFromJsonAsync<JsonElement>("/api/lidl-plus/coupons");

        body.GetProperty("state").GetString().Should().Be("connected");
        body.GetProperty("fresh").GetBoolean().Should().BeTrue();
        var coupons = Coupons(body);
        coupons.Select(c => c.GetProperty("id").GetString()).Should().Equal("C-3", "C-1", "C-2");
        coupons.Single(c => c.GetProperty("id").GetString() == "C-1").GetProperty("counts").GetBoolean().Should().BeFalse("it is not switched on");
        coupons.Single(c => c.GetProperty("id").GetString() == "C-2").GetProperty("counts").GetBoolean().Should().BeTrue();
        coupons.Single(c => c.GetProperty("id").GetString() == "C-3").GetProperty("counts").GetBoolean().Should().BeFalse("it has ended");
    }

    [Fact]
    public async Task A_coupon_that_left_the_list_leaves_the_snapshot()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Coupons.Add(Coupon("C-1", "Chicken fillets", "20% off")).Add(Coupon("C-2", "Oat biscuits", "€1 off"));
        await owner.GetAsync("/api/lidl-plus/coupons");
        factory.Coupons.Reset();
        factory.Coupons.Add(Coupon("C-2", "Oat biscuits", "€1 off"));

        var body = await owner.GetFromJsonAsync<JsonElement>("/api/lidl-plus/coupons");

        Coupons(body).Select(c => c.GetProperty("id").GetString()).Should().Equal("C-2");
    }

    [Fact]
    public async Task When_Lidl_cannot_be_asked_the_last_list_is_kept_and_marked_not_fresh()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Coupons.Add(Coupon("C-1", "Chicken fillets", "20% off"));
        await owner.GetAsync("/api/lidl-plus/coupons");
        factory.Coupons.ListFails = true;

        var response = await owner.GetAsync("/api/lidl-plus/coupons");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("fresh").GetBoolean().Should().BeFalse();
        Coupons(body).Should().ContainSingle();
    }

    [Fact]
    public async Task Without_a_connection_the_list_is_empty_and_activation_says_so()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await owner.DeleteAsync("/api/lidl-plus/connection");

        var body = await owner.GetFromJsonAsync<JsonElement>("/api/lidl-plus/coupons");

        body.GetProperty("state").GetString().Should().Be("not_connected");
        Coupons(body).Should().BeEmpty();
        (await owner.PostAsync("/api/lidl-plus/coupons/activate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Coupons.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_token_Lidl_rejects_asks_for_a_new_one()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Lidl.Then(LidlAuthResult.Rejected);

        (await owner.PostAsync("/api/lidl-plus/coupons/activate", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Lidl.Reset();
    }

    [Fact]
    public async Task One_coupon_is_switched_on_and_stays_on_in_the_list()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Coupons.Add(Coupon("C-1", "Chicken fillets", "20% off")).Add(Coupon("C-2", "Oat biscuits", "€1 off"));

        var response = await owner.PostAsync("/api/lidl-plus/coupons/C-1/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Coupons.Activations.Should().Equal("C-1");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("activated").GetInt32().Should().Be(1);
        Coupons(body).Single(c => c.GetProperty("id").GetString() == "C-1").GetProperty("activated").GetBoolean().Should().BeTrue();
        Coupons(body).Single(c => c.GetProperty("id").GetString() == "C-2").GetProperty("activated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Activate_all_switches_on_each_coupon_in_date_that_is_off_and_counts_what_Lidl_refused()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        var today = Today(factory);
        factory.Coupons
            .Add(Coupon("C-1", "Chicken fillets", "20% off"))
            .Add(Coupon("C-2", "Oat biscuits", "€1 off", activated: true))
            .Add(Coupon("C-3", "Ended", "10% off", until: today.AddDays(-1)))
            .Add(Coupon("C-4", "Not yet", "10% off", from: today.AddDays(2)))
            .Add(Coupon("C-5", "Refused", "10% off"));
        factory.Coupons.Refuses.Add("C-5");

        var response = await owner.PostAsync("/api/lidl-plus/coupons/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Coupons.Activations.Should().Equal("C-1");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("activated").GetInt32().Should().Be(1);
        body.GetProperty("refused").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_coupon_is_not_found_and_nothing_is_activated()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Coupons.Add(Coupon("C-1", "Chicken fillets", "20% off"));

        (await owner.PostAsync("/api/lidl-plus/coupons/C-9/activate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        factory.Coupons.Activations.Should().BeEmpty();
    }

    [Fact]
    public async Task When_Lidl_stops_answering_what_was_switched_on_stays_and_nothing_is_lost()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        var today = Today(factory);
        factory.Coupons
            .Add(Coupon("C-1", "Chicken fillets", "20% off", until: today.AddDays(1)))
            .Add(Coupon("C-2", "Oat biscuits", "€1 off", until: today.AddDays(2)));
        factory.Coupons.GoesAwayAt = "C-2";

        var response = await owner.PostAsync("/api/lidl-plus/coupons/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Coupons.Activations.Should().Equal("C-1");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activated").GetInt32().Should().Be(1);

        factory.Coupons.Reset();
        factory.Coupons.ListFails = true;
        (await owner.PostAsync("/api/lidl-plus/coupons/activate", null)).StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task The_coupons_are_the_owners_alone()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);

        (await factory.NewBrowser().GetAsync("/api/lidl-plus/coupons")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.NewBrowser().PostAsync("/api/lidl-plus/coupons/activate", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_active_coupon_makes_Lidl_the_cheaper_shop_on_the_promotions_and_the_plan_and_an_inactive_one_does_not()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        var today = Today(factory);
        await factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Chicken breast fillets", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            db.Products.Add(product);
            foreach (var (store, title, price) in new[] { (Store.Lidl, "Test chicken breast fillets 500g", 4.00m), (Store.Aldi, "Aldi chicken breast fillets 500g", 3.50m) })
            {
                var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = store, Url = $"https://example.test/{store}", Title = title, AddedAt = DateTimeOffset.UtcNow };
                db.StoreListings.Add(listing);
                db.PriceObservations.Add(new PriceObservation
                {
                    Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = today, ObservedAt = factory.Clock.GetUtcNow(), Price = price, InStock = true,
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });
        factory.Coupons.Add(Coupon("C-1", "Test chicken breast fillets", "20% off", until: today.AddDays(3)));
        await owner.GetAsync("/api/lidl-plus/coupons");

        JsonElement Lidl(JsonElement promotions) => promotions.GetProperty("products").EnumerateArray().Single()
            .GetProperty("offers").EnumerateArray().Single(o => o.GetProperty("store").GetString() == "lidl");

        // Only a product with a deal is listed, and nothing is on offer until the coupon is switched on.
        (await owner.GetFromJsonAsync<JsonElement>("/api/promotions")).GetProperty("products").GetArrayLength().Should().Be(0);

        (await owner.PostAsync("/api/lidl-plus/coupons/C-1/activate", null)).EnsureSuccessStatusCode();

        var after = Lidl(await owner.GetFromJsonAsync<JsonElement>("/api/promotions"));
        after.GetProperty("kind").GetString().Should().Be("coupon");
        after.GetProperty("eachPrice").GetDecimal().Should().Be(3.20m);
        after.GetProperty("validUntil").GetString().Should().Be(today.AddDays(3).ToString("yyyy-MM-dd"));
    }
}
