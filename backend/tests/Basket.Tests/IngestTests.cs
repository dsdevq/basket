using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Ingest;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The write-only price endpoint the PC-side reader posts Tesco and Aldi readings to.</summary>
public sealed class IngestTests : IClassFixture<BasketFactory>
{
    private const string TescoUrl = "https://www.tesco.ie/groceries/en-IE/products/254317390";
    private const string AldiUrl = "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541";
    private const string LidlUrl = "https://www.lidl.ie/p/aubergine/p11214436";

    private readonly BasketFactory _factory;

    public IngestTests(BasketFactory factory) => _factory = factory;

    /// <summary>Starts from no products, runs or tokens, with Milk linked to the Tesco and Aldi pages above.</summary>
    private async Task<HttpClient> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            await db.IngestTokens.ExecuteDeleteAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            db.Products.Add(product);
            db.StoreListings.AddRange(
                new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Tesco, Url = TescoUrl, AddedAt = DateTimeOffset.UtcNow },
                new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Aldi, Url = AldiUrl, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        return owner;
    }

    private static async Task<(Guid Id, string Token)> MintAsync(HttpClient owner, string? label = "Home PC")
    {
        var response = await owner.PostAsJsonAsync("/api/ingest-tokens", new { label });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    /// <summary>The reader: a plain HTTP client with no cookie and no Origin header, only the bearer token.</summary>
    private HttpClient Relay(string? token)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, params object[] prices) =>
        client.PostAsJsonAsync("/api/ingest/prices", new { prices });

    private static object Tesco(decimal? price = 2.15m, string url = TescoUrl) => new { store = "tesco", url, price };

    private static string[] Reasons(JsonElement body) =>
        body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("reason").GetString() ?? "ok").ToArray();

    [Fact]
    public async Task The_owner_mints_a_token_shown_once_and_stored_only_as_a_hash()
    {
        var owner = await SeedAsync();

        var (id, token) = await MintAsync(owner);

        token.Should().StartWith("bsk_").And.HaveLength(4 + 43);
        await _factory.WithDb(async db =>
        {
            var stored = await db.IngestTokens.SingleAsync();
            stored.Id.Should().Be(id);
            stored.TokenHash.Should().Be(IngestTokens.Hash(token)).And.NotContain(token);
            stored.Label.Should().Be("Home PC");
            return 0;
        });

        var listed = await owner.GetStringAsync("/api/ingest-tokens");
        listed.Should().Contain("Home PC").And.NotContain(token).And.NotContain(IngestTokens.Hash(token));
    }

    [Fact]
    public async Task A_revoked_token_stops_working_at_once_and_revoking_twice_is_a_404()
    {
        var owner = await SeedAsync();
        var (id, token) = await MintAsync(owner);
        (await PostAsync(Relay(token), Tesco())).StatusCode.Should().Be(HttpStatusCode.OK);

        (await owner.DeleteAsync($"/api/ingest-tokens/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await PostAsync(Relay(token), Tesco())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await owner.DeleteAsync($"/api/ingest-tokens/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_token_that_is_missing_wrong_or_not_a_bearer_is_refused_and_asks_for_a_bearer()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var none = await PostAsync(Relay(null), Tesco());
        var wrong = await PostAsync(Relay("bsk_" + new string('A', 43)), Tesco());
        var basic = Relay(null);
        basic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        var notBearer = await PostAsync(basic, Tesco());

        foreach (var response in new[] { none, wrong, notBearer })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
        }
    }

    [Fact]
    public async Task A_signed_in_session_is_not_a_token()
    {
        var owner = await SeedAsync();

        var response = await PostAsync(owner, Tesco());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.WithDb(db => db.PriceObservations.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_token_opens_nothing_else_and_reads_nothing()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);
        var relay = Relay(token);

        foreach (var path in new[] { "/api/products", "/api/list", "/api/ingest-tokens", "/api/members", "/api/invites", "/api/passkeys" })
        {
            (await relay.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        }

        (await relay.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("authenticated").GetBoolean().Should().BeFalse();

        // Without an Origin the other endpoints' CSRF check already says no; with one, the missing session does.
        HttpStatusCode[] refused = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];
        (await relay.PostAsJsonAsync("/api/ingest-tokens", new { label = "mine" })).StatusCode.Should().BeOneOf(refused);
        (await relay.PostAsJsonAsync("/api/products", new { name = "Bread" })).StatusCode.Should().BeOneOf(refused);
        relay.DefaultRequestHeaders.Add("Origin", BasketFactory.Origin);
        (await relay.PostAsJsonAsync("/api/ingest-tokens", new { label = "mine" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await relay.PostAsJsonAsync("/api/products", new { name = "Bread" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await relay.GetAsync("/api/ingest/prices")).StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_recorded_relay_batch_becomes_todays_observations_through_the_shared_write_path()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);
        var batch = Fixtures.Read("ingest-batch.json");

        var response = await Relay(token).PostAsync("/api/ingest/prices", new StringContent(batch, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(2);
        body.GetProperty("rejected").GetInt32().Should().Be(0);

        var today = PriceRecorder.DublinDay(_factory.Clock.GetUtcNow());
        await _factory.WithDb(async db =>
        {
            var tesco = await db.PriceObservations.Join(db.StoreListings, o => o.ListingId, l => l.Id, (o, l) => new { o, l }).SingleAsync(x => x.l.Store == Store.Tesco);
            tesco.o.ObservedOn.Should().Be(today);
            tesco.o.ObservedAt.Should().BeCloseTo(_factory.Clock.GetUtcNow(), TimeSpan.FromMilliseconds(1));
            tesco.o.Price.Should().Be(2.15m);
            tesco.o.WasPrice.Should().Be(2.35m);
            tesco.o.LoyaltyPrice.Should().Be(1.89m);
            tesco.o.LoyaltyScheme.Should().Be("Clubcard");
            tesco.o.PromoText.Should().Be("Clubcard Price");
            tesco.o.ValidFrom.Should().Be(new DateOnly(2026, 11, 2));
            tesco.o.ValidUntil.Should().Be(new DateOnly(2026, 11, 15));
            tesco.o.InStock.Should().BeTrue();
            tesco.l.Title.Should().Be("Tesco Fresh Whole Milk 2 Litre");
            tesco.l.LastCheckedAt.Should().BeCloseTo(_factory.Clock.GetUtcNow(), TimeSpan.FromMilliseconds(1));
            tesco.l.LastError.Should().BeNull();

            var aldi = await db.PriceObservations.Join(db.StoreListings, o => o.ListingId, l => l.Id, (o, l) => new { o, l }).SingleAsync(x => x.l.Store == Store.Aldi);
            aldi.o.Price.Should().Be(1.19m);
            aldi.o.LoyaltyPrice.Should().BeNull();
            aldi.o.LoyaltyScheme.Should().BeNull();

            var runs = await db.PricePullRuns.OrderBy(r => r.Store).ToListAsync();
            runs.Select(r => (r.Store, r.Outcome, r.Listings, r.Read)).Should().Equal(
                (Store.Aldi, PullOutcome.Ok, 1, 1),
                (Store.Tesco, PullOutcome.Ok, 1, 1));
            return 0;
        });

        var products = await owner.GetFromJsonAsync<JsonElement>("/api/products");
        var tescoEntry = products.GetProperty("products")[0].GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "tesco");
        tescoEntry.GetProperty("price").GetProperty("best").GetDecimal().Should().Be(1.89m);
    }

    [Fact]
    public async Task Posting_the_same_listing_again_the_same_day_replaces_that_days_reading()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);
        var relay = Relay(token);

        (await PostAsync(relay, Tesco(2.15m))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(relay, Tesco(1.99m))).StatusCode.Should().Be(HttpStatusCode.OK);

        await _factory.WithDb(async db =>
        {
            (await db.PriceObservations.SingleAsync()).Price.Should().Be(1.99m);
            return 0;
        });
    }

    [Fact]
    public async Task A_reading_in_the_shop_address_form_matches_the_listing_stored_in_the_groceries_form()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(Relay(token), Tesco(1.99m, "https://www.tesco.ie/shop/en-IE/products/254317390?x=1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Reasons(await response.Content.ReadFromJsonAsync<JsonElement>()).Should().Equal("ok");
        await _factory.WithDb(async db =>
        {
            (await db.PriceObservations.SingleAsync()).Price.Should().Be(1.99m);
            (await db.StoreListings.Where(l => l.Store == Store.Tesco).SingleAsync()).Url.Should().Be(TescoUrl);
            return 0;
        });
    }

    [Fact]
    public async Task Two_products_linked_to_the_same_page_both_get_the_reading_and_it_counts_as_accepted_once()
    {
        var owner = await SeedAsync();
        await _factory.WithDb(async db =>
        {
            var second = new Product { Id = Guid.NewGuid(), Name = "Semi-skimmed milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            db.Products.Add(second);
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = second.Id, Store = Store.Tesco, Url = TescoUrl, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(Relay(token), Tesco(2.15m), Tesco(1.99m));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(1);
        body.GetProperty("rejected").GetInt32().Should().Be(1);
        Reasons(body).Should().Equal("ok", "duplicate");
        await _factory.WithDb(async db =>
        {
            var tesco = await db.PriceObservations.Join(db.StoreListings, o => o.ListingId, l => l.Id, (o, l) => new { o, l.Store }).Where(x => x.Store == Store.Tesco).ToListAsync();
            tesco.Should().HaveCount(2);
            tesco.Select(x => x.o.Price).Should().OnlyContain(p => p == 2.15m);
            return 0;
        });
    }

    [Fact]
    public async Task A_loyalty_price_without_a_scheme_takes_the_shops_card_and_a_scheme_without_a_price_is_dropped()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(
            Relay(token),
            new { store = "tesco", url = TescoUrl, price = 2.15m, loyaltyPrice = 1.89m },
            new { store = "aldi", url = AldiUrl, price = 1.19m, loyaltyScheme = "Aldi Club" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.WithDb(async db =>
        {
            var rows = await db.PriceObservations.Join(db.StoreListings, o => o.ListingId, l => l.Id, (o, l) => new { l.Store, o.LoyaltyScheme }).ToListAsync();
            rows.Single(r => r.Store == Store.Tesco).LoyaltyScheme.Should().Be("Clubcard");
            rows.Single(r => r.Store == Store.Aldi).LoyaltyScheme.Should().BeNull();
            return 0;
        });
    }

    [Fact]
    public async Task An_out_of_stock_listing_without_a_price_is_a_valid_reading()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(Relay(token), new { store = "aldi", url = AldiUrl, inStock = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.WithDb(async db =>
        {
            var observation = await db.PriceObservations.SingleAsync();
            observation.Price.Should().BeNull();
            observation.InStock.Should().BeFalse();
            return 0;
        });
    }

    [Fact]
    public async Task Bad_readings_are_refused_one_by_one_each_with_its_reason_and_the_good_ones_are_kept()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(
            Relay(token),
            Tesco(2.15m),
            new { store = "waitrose", url = TescoUrl, price = 1m },
            new { store = "lidl", url = LidlUrl, price = 1m },
            new { store = "tesco", url = "https://evil.example/groceries/en-IE/products/254317390", price = 1m },
            new { store = "tesco", url = "https://www.tesco.ie/groceries/en-IE/products/999999999", price = 1m },
            new { store = "aldi", url = AldiUrl, price = -1.19m },
            new { store = "aldi", url = AldiUrl, price = 0m },
            new { store = "aldi", url = AldiUrl, price = 1.199m },
            new { store = "aldi", url = AldiUrl, price = 100000m },
            new { store = "aldi", url = AldiUrl, loyaltyPrice = -0.5m, price = 1.19m },
            new { store = "aldi", url = AldiUrl },
            new { store = "aldi", url = AldiUrl, price = 1.19m, validFrom = "2026-11-10", validUntil = "2026-11-02" },
            Tesco(1.50m));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Reasons(body).Should().Equal(
            "ok",
            "unknown_store",
            "store_not_ingested",
            "url_invalid",
            "unknown_listing",
            "price_invalid",
            "price_invalid",
            "price_invalid",
            "price_invalid",
            "price_invalid",
            "no_price",
            "dates_invalid",
            "duplicate");
        body.GetProperty("accepted").GetInt32().Should().Be(1);
        body.GetProperty("rejected").GetInt32().Should().Be(12);

        await _factory.WithDb(async db =>
        {
            (await db.PriceObservations.SingleAsync()).Price.Should().Be(2.15m, "the first reading of a listing in a batch wins");
            (await db.PricePullRuns.CountAsync(r => r.Store == Store.Lidl)).Should().Be(0, "a refused store is not a run");
            var tescoRun = await db.PricePullRuns.SingleAsync(r => r.Store == Store.Tesco);
            tescoRun.Outcome.Should().Be(PullOutcome.Partial);
            tescoRun.Message.Should().Contain("duplicate").And.Contain("unknown_listing");
            return 0;
        });
    }

    [Fact]
    public async Task A_batch_where_nothing_is_usable_is_a_422_and_marks_the_shop_failed()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(Relay(token), Tesco(-2m));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32().Should().Be(0);
        (await _factory.WithDb(db => db.PriceObservations.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Text_in_a_reading_is_cleaned_and_capped_before_it_is_stored()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await PostAsync(
            Relay(token),
            new { store = "tesco", url = TescoUrl, price = 2.15m, title = "  Milk \u0007  2L ", promoText = new string('x', 500), loyaltyPrice = 1.9m, loyaltyScheme = new string('y', 90) });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.WithDb(async db =>
        {
            var observation = await db.PriceObservations.SingleAsync();
            observation.PromoText.Should().HaveLength(200);
            observation.LoyaltyScheme.Should().HaveLength(32);
            (await db.StoreListings.SingleAsync(l => l.Store == Store.Tesco)).Title.Should().Be("Milk 2L");
            return 0;
        });
    }

    [Theory]
    [InlineData("""{"prices":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"prices":[null]}""")]
    [InlineData("""{"prices":[{"store":"tesco","price":"cheap"}]}""")]
    [InlineData("""not json""")]
    public async Task A_body_that_is_not_a_batch_is_a_400(string json)
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);

        var response = await Relay(token).PostAsync("/api/ingest/prices", new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_batch_over_the_limit_and_a_body_over_the_size_cap_are_refused()
    {
        var owner = await SeedAsync();
        var (_, token) = await MintAsync(owner);
        var relay = Relay(token);

        var tooMany = await PostAsync(relay, Enumerable.Repeat<object>(Tesco(), PriceIngest.MaxBatch + 1).ToArray());
        var tooBig = await relay.PostAsync(
            "/api/ingest/prices",
            new StringContent("{\"prices\":[],\"pad\":\"" + new string('x', IngestEndpoints.MaxBodyBytes) + "\"}", Encoding.UTF8, "application/json"));
        var notJson = await relay.PostAsync("/api/ingest/prices", new StringContent("prices", Encoding.UTF8, "text/plain"));

        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooBig.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        notJson.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task A_caller_without_a_token_learns_nothing_about_the_batch_it_sent()
    {
        await SeedAsync();

        var response = await Relay(null).PostAsync("/api/ingest/prices", new StringContent("not json", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_ingest_endpoint_is_rate_limited_per_client()
    {
        await using var limited = new BasketFactory();
        var (owner, _) = await limited.OwnerAsync();
        var token = (await (await owner.PostAsJsonAsync("/api/ingest-tokens", new { label = "pc" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var client = limited.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin) });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 32; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/ingest/prices", new { prices = Array.Empty<object>() })).StatusCode);
        }

        statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.BadRequest);
        statuses.Skip(30).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }
}
