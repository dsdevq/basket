using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Ingest;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// The write-only catalog endpoints the PC reader posts a whole shop's catalog to, beside the price endpoint and behind the
/// same token, scope, limiter and size cap. Synthetic products only.
/// </summary>
public sealed class CatalogIngestTests : IClassFixture<CatalogIngestTests.RoomyFactory>
{
    /// <summary>The whole class posts from one client in a few seconds; the limit itself is tested on its own host below.</summary>
    public sealed class RoomyFactory : BasketFactory
    {
        public RoomyFactory() => IngestRequestsPerMinute = 1000;
    }

    private const string Catalog = "/api/ingest/catalog";
    private const string Complete = "/api/ingest/catalog/complete";

    private readonly BasketFactory _factory;

    public CatalogIngestTests(RoomyFactory factory) => _factory = factory;

    private async Task<HttpClient> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            await db.PriceObservations.ExecuteDeleteAsync();
            await db.IngestTokens.ExecuteDeleteAsync();
            return 0;
        });
        return owner;
    }

    private static async Task<string> MintAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/ingest-tokens", new { label = "Home PC" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static HttpClient Relay(BasketFactory factory, string? token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private HttpClient Relay(string? token) => Relay(_factory, token);

    private async Task<HttpClient> ReaderAsync() => Relay(await MintAsync(await SeedAsync()));

    private static object Tesco(int n, decimal? price = 2.15m, string? name = null) => new
    {
        externalId = $"2543{n:D5}",
        url = $"https://www.tesco.ie/groceries/en-IE/products/2543{n:D5}",
        name = name ?? $"Test Milk {n}",
        sizeText = "2 litre",
        price,
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string store, params object[] items) =>
        client.PostAsJsonAsync(Catalog, new { store, items });

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string store, int seen, DateTimeOffset startedAt) =>
        client.PostAsJsonAsync(Complete, new { store, seen, startedAt });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Reasons(JsonElement body) =>
        body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("reason").GetString() ?? "ok").ToArray();

    // ---- who may post -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_without_a_token_learns_nothing_about_the_batch_it_sent()
    {
        await SeedAsync();

        var batch = await Relay(null).PostAsync(Catalog, new StringContent("not json", Encoding.UTF8, "application/json"));
        var complete = await Relay(null).PostAsync(Complete, new StringContent("not json", Encoding.UTF8, "application/json"));

        batch.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        complete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_revoked_or_made_up_token_is_refused()
    {
        var owner = await SeedAsync();

        var made = await PostAsync(Relay("bsk_" + new string('A', 43)), "tesco", Tesco(1));
        var token = await MintAsync(owner);
        var id = (await (await owner.GetAsync("/api/ingest-tokens")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single().GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/ingest-tokens/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var revoked = await PostAsync(Relay(token), "tesco", Tesco(1));

        made.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_owner_minted_token_posts_a_catalog_batch()
    {
        var reader = await ReaderAsync();

        var response = await PostAsync(reader, "tesco", Tesco(1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).GetProperty("accepted").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_fleet_token_with_the_price_ingest_scope_posts_a_batch_and_closes_a_sweep_with_no_new_scope()
    {
        await SeedAsync();
        var fleet = Relay(_factory.Fleet.Mint(string.Join(' ', "fleet:read", FleetScopes.PriceIngest)));

        var batch = await PostAsync(fleet, "tesco", Tesco(1));
        var complete = await CompleteAsync(fleet, "tesco", 1, _factory.Clock.GetUtcNow().AddMinutes(-5));

        batch.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(FleetScopes.CatalogRead)]
    [InlineData(FleetScopes.CatalogAdd)]
    [InlineData("fleet:read")]
    [InlineData("basket:price-ingest-all")]
    public async Task A_fleet_token_without_the_ingest_scope_is_forbidden_and_writes_nothing(string scope)
    {
        await SeedAsync();
        var fleet = Relay(_factory.Fleet.Mint(scope));

        var batch = await PostAsync(fleet, "tesco", Tesco(1));
        var complete = await CompleteAsync(fleet, "tesco", 1, _factory.Clock.GetUtcNow().AddMinutes(-5));

        batch.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        complete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(0);
        (await _factory.WithDb(db => db.PricePullRuns.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_cookie_session_cannot_post_a_batch()
    {
        var owner = await SeedAsync();

        var response = await PostAsync(owner, "tesco", Tesco(1));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- the batch --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_posted_fixture_batch_is_stored_with_its_normalised_fields()
    {
        var reader = await ReaderAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-batch.json"));

        var response = await reader.PostAsync(Catalog, new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("accepted").GetInt32().Should().Be(3);
        body.GetProperty("rejected").GetInt32().Should().Be(0);
        await _factory.WithDb(async db =>
        {
            var mozzarella = await db.StoreProducts.SingleAsync(p => p.ExternalId == "000000000000900101");
            mozzarella.Store.Should().Be(Store.Aldi);
            mozzarella.Name.Should().Be("Grated Mozzarella");
            mozzarella.Brand.Should().Be("GREENFIELD");
            (mozzarella.SizeAmount, mozzarella.SizeUnit, mozzarella.SizePacks).Should().Be((250m, "g", 1));
            (mozzarella.UnitPrice, mozzarella.UnitPriceBasis).Should().Be((5.96m, "1 KG"));
            mozzarella.Barcode.Should().BeNull();
            mozzarella.Category.Should().Be("Chilled Food > Cheese");
            mozzarella.ImageUrl.Should().Be("https://images.example.test/product/jpg/scaleWidth/300/aaaa-0001");

            var water = await db.StoreProducts.SingleAsync(p => p.ExternalId == "000000000000900103");
            (water.SizeAmount, water.SizeUnit, water.SizePacks).Should().Be((330m, "ml", 6));

            var slices = await db.StoreProducts.SingleAsync(p => p.ExternalId == "000000000000900102");
            var price = await db.StoreProductPrices.SingleAsync(p => p.StoreProductId == slices.Id);
            (price.Price, price.WasPrice).Should().Be((1.29m, 1.35m));
            return 0;
        });
    }

    [Fact]
    public async Task Each_item_is_judged_alone_with_a_stable_reason_and_the_others_are_kept()
    {
        var reader = await ReaderAsync();

        var response = await PostAsync(
            reader,
            "tesco",
            Tesco(1),
            new { externalId = "", url = "https://www.tesco.ie/groceries/en-IE/products/254300002", name = "A" },
            new { externalId = "254300003", url = "https://www.tesco.ie/elsewhere", name = "B" },
            new { externalId = "254300004", url = "https://www.tesco.ie/groceries/en-IE/products/254300004", name = " " },
            new { externalId = "254300005", url = "https://www.tesco.ie/groceries/en-IE/products/254300005", name = "E", price = -1 },
            new { externalId = "254300006", url = "https://www.tesco.ie/groceries/en-IE/products/254300006", name = "F", validFrom = "2026-11-20", validUntil = "2026-11-10" },
            Tesco(1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        Reasons(body).Should().Equal("ok", "external_id_invalid", "url_invalid", "no_name", "price_invalid", "dates_invalid", "duplicate");
        body.GetProperty("accepted").GetInt32().Should().Be(1);
        body.GetProperty("rejected").GetInt32().Should().Be(6);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_size_too_large_for_the_column_costs_the_product_its_size_and_not_the_batch()
    {
        var reader = await ReaderAsync();

        var response = await PostAsync(
            reader,
            "tesco",
            Tesco(1),
            new { externalId = "254300002", url = "https://www.tesco.ie/groceries/en-IE/products/254300002", name = "Rice", sizeText = "12345678901 g", price = 2.15m },
            new { externalId = "254300003", url = "https://www.tesco.ie/groceries/en-IE/products/254300003", name = "Flour 1234567890123456789012345678901234567890 g", price = 2.15m });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Reasons(await BodyAsync(response)).Should().Equal("ok", "ok", "ok");
        var sizes = await _factory.WithDb(db => db.StoreProducts.OrderBy(p => p.ExternalId).Select(p => p.SizeAmount).ToListAsync());
        sizes.Should().Equal(2000m, null, null);
    }

    [Fact]
    public async Task A_batch_with_nothing_usable_is_a_422_and_writes_nothing()
    {
        var reader = await ReaderAsync();

        var response = await PostAsync(reader, "tesco", new { externalId = "1", url = "https://www.tesco.ie/nope", name = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Reasons(await BodyAsync(response)).Should().Equal("url_invalid");
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData("lidl", "store_not_ingested")]
    [InlineData("superquinn", "unknown_store")]
    [InlineData(null, "unknown_store")]
    public async Task A_shop_the_server_reads_itself_or_does_not_know_is_refused_item_by_item(string? store, string reason)
    {
        var reader = await ReaderAsync();

        var response = await PostAsync(reader, store!, Tesco(1), Tesco(2));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Reasons(await BodyAsync(response)).Should().Equal(reason, reason);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Sending_the_same_batch_twice_changes_nothing_the_second_time()
    {
        var reader = await ReaderAsync();
        await PostAsync(reader, "tesco", Tesco(1), Tesco(2));
        _factory.Clock.Advance(TimeSpan.FromHours(1));

        var again = await PostAsync(reader, "tesco", Tesco(1), Tesco(2));

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync())).Should().Be(2);
        (await _factory.WithDb(db => db.StoreProductPrices.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task A_price_history_row_appears_only_on_the_day_the_price_changes()
    {
        var reader = await ReaderAsync();
        await PostAsync(reader, "tesco", Tesco(1, 2.15m));
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        await PostAsync(reader, "tesco", Tesco(1, 2.15m));
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        await PostAsync(reader, "tesco", Tesco(1, 1.99m));
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        await PostAsync(reader, "tesco", Tesco(1, 1.99m));

        var prices = await _factory.WithDb(db => db.StoreProductPrices.OrderBy(p => p.ObservedOn).Select(p => p.Price).ToListAsync());

        prices.Should().Equal(2.15m, 1.99m);
    }

    [Fact]
    public async Task The_server_dates_the_product_not_the_poster()
    {
        var reader = await ReaderAsync();

        await reader.PostAsJsonAsync(Catalog, new { store = "tesco", items = new object[] { Tesco(1) }, seenAt = "2001-01-01T00:00:00Z" });

        var product = await _factory.WithDb(db => db.StoreProducts.SingleAsync());
        product.LastSeenAt.Should().BeCloseTo(_factory.Clock.GetUtcNow(), TimeSpan.FromMilliseconds(1));
        product.FirstSeenAt.Should().Be(product.LastSeenAt);
    }

    [Fact]
    public async Task The_price_endpoint_and_the_shelf_readings_are_untouched_by_a_catalog_batch()
    {
        var reader = await ReaderAsync();

        await PostAsync(reader, "tesco", Tesco(1));

        (await _factory.WithDb(db => db.PriceObservations.CountAsync())).Should().Be(0);
        (await _factory.WithDb(db => db.PricePullRuns.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"store":"tesco","items":[null]}""")]
    [InlineData("""{"store":"tesco","items":[{"price":"cheap"}]}""")]
    [InlineData("""not json""")]
    public async Task A_body_that_is_not_a_batch_is_a_400(string json)
    {
        var reader = await ReaderAsync();

        var response = await reader.PostAsync(Catalog, new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_batch_over_the_limit_a_body_over_the_size_cap_and_a_non_json_body_are_refused()
    {
        var reader = await ReaderAsync();

        var tooMany = await PostAsync(reader, "tesco", Enumerable.Range(0, CatalogIngest.MaxBatch + 1).Select(n => Tesco(n)).ToArray());
        var atLimit = await PostAsync(reader, "tesco", Enumerable.Range(0, CatalogIngest.MaxBatch).Select(n => Tesco(n)).ToArray());
        var tooBig = await reader.PostAsync(
            Catalog,
            new StringContent("{\"items\":[],\"pad\":\"" + new string('x', IngestEndpoints.MaxBodyBytes) + "\"}", Encoding.UTF8, "application/json"));
        var notJson = await reader.PostAsync(Catalog, new StringContent("items", Encoding.UTF8, "text/plain"));

        CatalogIngest.MaxBatch.Should().Be(200);
        IngestEndpoints.MaxBodyBytes.Should().Be(256 * 1024);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        atLimit.StatusCode.Should().Be(HttpStatusCode.OK);
        tooBig.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        notJson.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task The_catalog_endpoints_are_rate_limited_at_thirty_a_minute_per_client_across_both_routes()
    {
        await using var limited = new BasketFactory();
        var (owner, _) = await limited.OwnerAsync();
        var client = Relay(limited, await MintAsync(owner));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 32; i++)
        {
            var path = i % 2 == 0 ? Catalog : Complete;
            statuses.Add((await client.PostAsJsonAsync(path, new { items = Array.Empty<object>() })).StatusCode);
        }

        statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.BadRequest);
        statuses.Skip(30).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }

    // ---- closing a sweep --------------------------------------------------------------------------------------

    /// <summary>A first sweep of <paramref name="count"/> products, closed, then the clock moved on an hour; returns when the next sweep starts.</summary>
    private async Task<DateTimeOffset> FirstSweepAsync(HttpClient reader, int count)
    {
        var started = _factory.Clock.GetUtcNow();
        var response = await PostAsync(reader, "tesco", Enumerable.Range(0, count).Select(n => Tesco(n)).ToArray());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CompleteAsync(reader, "tesco", count, started)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Clock.Advance(TimeSpan.FromHours(1));
        return _factory.Clock.GetUtcNow();
    }

    [Fact]
    public async Task The_first_sweep_closes_with_a_run_and_discontinues_nothing()
    {
        var reader = await ReaderAsync();
        var started = _factory.Clock.GetUtcNow();
        await PostAsync(reader, "tesco", Tesco(1), Tesco(2));

        var response = await CompleteAsync(reader, "tesco", 2, started);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("store").GetString().Should().Be("tesco");
        body.GetProperty("discontinued").GetInt32().Should().Be(0);
        body.GetProperty("guarded").GetBoolean().Should().BeFalse();
        body.GetProperty("outcome").GetString().Should().Be("Ok");
        var run = await _factory.WithDb(db => db.PricePullRuns.SingleAsync());
        (run.Store, run.Outcome, run.Listings, run.Read).Should().Be((Store.Tesco, PullOutcome.Ok, 2, 2));
        run.StartedAt.Should().BeCloseTo(started, TimeSpan.FromMilliseconds(1));
        run.Message.Should().BeNull();
    }

    [Fact]
    public async Task A_sweep_that_saw_nearly_everything_marks_the_unseen_products_discontinued()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 10);
        await PostAsync(reader, "tesco", Enumerable.Range(0, 9).Select(n => Tesco(n)).ToArray());

        var response = await CompleteAsync(reader, "tesco", 9, started);

        var body = await BodyAsync(response);
        body.GetProperty("discontinued").GetInt32().Should().Be(1);
        body.GetProperty("previousLive").GetInt32().Should().Be(10);
        body.GetProperty("guarded").GetBoolean().Should().BeFalse();
        await _factory.WithDb(async db =>
        {
            (await db.StoreProducts.Where(p => p.DiscontinuedAt != null).Select(p => p.ExternalId).SingleAsync()).Should().Be("254300009");
            var run = await db.PricePullRuns.OrderByDescending(r => r.FinishedAt).FirstAsync();
            (run.Outcome, run.Listings, run.Read).Should().Be((PullOutcome.Ok, 9, 9));
            run.Message.Should().Contain("1 products");
            return 0;
        });
    }

    [Fact]
    public async Task A_half_failed_sweep_marks_nothing_discontinued_and_says_so_in_its_run()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 10);
        await PostAsync(reader, "tesco", Enumerable.Range(0, 8).Select(n => Tesco(n)).ToArray());

        var response = await CompleteAsync(reader, "tesco", 8, started);

        var body = await BodyAsync(response);
        body.GetProperty("guarded").GetBoolean().Should().BeTrue();
        body.GetProperty("discontinued").GetInt32().Should().Be(0);
        body.GetProperty("outcome").GetString().Should().Be("Partial");
        await _factory.WithDb(async db =>
        {
            (await db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null)).Should().Be(0);
            var run = await db.PricePullRuns.OrderByDescending(r => r.FinishedAt).FirstAsync();
            run.Outcome.Should().Be(PullOutcome.Partial);
            run.Message.Should().Contain("8 of the 10").And.Contain("nothing was marked discontinued");
            return 0;
        });
    }

    [Fact]
    public async Task A_sweep_that_saw_nothing_is_a_failed_run_and_delists_nothing()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 5);

        var response = await CompleteAsync(reader, "tesco", 0, started);

        (await BodyAsync(response)).GetProperty("outcome").GetString().Should().Be("Failed");
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task A_first_sweep_that_listed_nothing_is_a_failed_run()
    {
        var reader = await ReaderAsync();
        var started = _factory.Clock.GetUtcNow();

        var response = await CompleteAsync(reader, "tesco", 0, started);

        var body = await BodyAsync(response);
        body.GetProperty("outcome").GetString().Should().Be("Failed");
        body.GetProperty("guarded").GetBoolean().Should().BeTrue();
        var run = await _factory.WithDb(db => db.PricePullRuns.SingleAsync());
        (run.Outcome, run.Listings, run.Read).Should().Be((PullOutcome.Failed, 0, 0));
        run.Message.Should().Contain("listed 0 products");
    }

    [Fact]
    public async Task A_sweep_of_a_shop_whose_products_were_all_discontinued_that_lists_nothing_is_a_failed_run()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 2);
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteUpdateAsync(s => s.SetProperty(p => p.DiscontinuedAt, started));
            return 0;
        });

        var response = await CompleteAsync(reader, "tesco", 0, started);

        (await BodyAsync(response)).GetProperty("outcome").GetString().Should().Be("Failed");
    }

    [Fact]
    public async Task One_item_rejected_out_of_a_thousand_known_still_passes_the_guard_and_delists_only_the_unseen_one()
    {
        var reader = await ReaderAsync();
        var first = _factory.Clock.GetUtcNow();
        foreach (var chunk in Enumerable.Range(0, 1000).Chunk(CatalogIngest.MaxBatch))
        {
            (await PostAsync(reader, "tesco", chunk.Select(n => Tesco(n)).ToArray())).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await CompleteAsync(reader, "tesco", 1000, first)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Clock.Advance(TimeSpan.FromHours(1));
        var started = _factory.Clock.GetUtcNow();

        // The reader parses all 1000 and counts 1000, but one of them (the last) is refused with no_name, so the server saw 999.
        foreach (var chunk in Enumerable.Range(0, 999).Chunk(CatalogIngest.MaxBatch))
        {
            await PostAsync(reader, "tesco", chunk.Select(n => Tesco(n)).ToArray());
        }

        var rejected = await PostAsync(reader, "tesco", Tesco(999, name: " "));
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var response = await CompleteAsync(reader, "tesco", 1000, started);

        var body = await BodyAsync(response);
        body.GetProperty("guarded").GetBoolean().Should().BeFalse();
        body.GetProperty("outcome").GetString().Should().Be("Ok");
        body.GetProperty("discontinued").GetInt32().Should().Be(1);
        (await _factory.WithDb(db => db.StoreProducts.Where(p => p.DiscontinuedAt != null).Select(p => p.ExternalId).SingleAsync())).Should().Be("254300999");
    }

    [Fact]
    public async Task A_first_sweep_whose_reader_counts_more_than_the_server_saw_is_not_recorded_as_healthy()
    {
        var reader = await ReaderAsync();
        var started = _factory.Clock.GetUtcNow();
        await PostAsync(reader, "tesco", Tesco(1), Tesco(2));

        var response = await CompleteAsync(reader, "tesco", 50, started);

        var body = await BodyAsync(response);
        body.GetProperty("guarded").GetBoolean().Should().BeTrue();
        body.GetProperty("outcome").GetString().Should().Be("Partial");
        var run = await _factory.WithDb(db => db.PricePullRuns.SingleAsync());
        (run.Outcome, run.Read).Should().Be((PullOutcome.Partial, 2));
        run.Message.Should().Contain("counted 50");
    }

    [Fact]
    public async Task A_reader_that_claims_more_than_it_posted_cannot_get_the_rest_delisted()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 10);
        await PostAsync(reader, "tesco", Enumerable.Range(0, 5).Select(n => Tesco(n)).ToArray());

        var response = await CompleteAsync(reader, "tesco", 10, started);

        (await BodyAsync(response)).GetProperty("guarded").GetBoolean().Should().BeTrue();
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task New_products_in_a_sweep_do_not_make_up_for_known_ones_it_missed()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 10);
        await PostAsync(reader, "tesco", Enumerable.Range(100, 40).Select(n => Tesco(n)).Concat(Enumerable.Range(0, 5).Select(n => Tesco(n))).ToArray());

        var response = await CompleteAsync(reader, "tesco", 45, started);

        (await BodyAsync(response)).GetProperty("guarded").GetBoolean().Should().BeTrue();
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task A_sweep_never_touches_another_shops_products()
    {
        var reader = await ReaderAsync();
        var started = await FirstSweepAsync(reader, 3);
        await PostAsync(reader, "dunnes", new { externalId = "100317581", url = "https://www.dunnesstoresgrocery.com/product/test-swede-100317581", name = "Test Swede", price = 0.99m });

        var response = await CompleteAsync(reader, "dunnes", 1, started);

        (await BodyAsync(response)).GetProperty("discontinued").GetInt32().Should().Be(0);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.Store == Store.Tesco && p.DiscontinuedAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task Discontinued_products_do_not_count_toward_the_next_sweeps_known_total()
    {
        var reader = await ReaderAsync();
        var second = await FirstSweepAsync(reader, 10);
        await PostAsync(reader, "tesco", Enumerable.Range(0, 9).Select(n => Tesco(n)).ToArray());
        await CompleteAsync(reader, "tesco", 9, second);
        _factory.Clock.Advance(TimeSpan.FromHours(1));
        var third = _factory.Clock.GetUtcNow();
        await PostAsync(reader, "tesco", Enumerable.Range(0, 9).Select(n => Tesco(n)).ToArray());

        var response = await CompleteAsync(reader, "tesco", 9, third);

        var body = await BodyAsync(response);
        body.GetProperty("previousLive").GetInt32().Should().Be(9);
        body.GetProperty("guarded").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"store":"tesco","seen":3}""")]
    [InlineData("""{"store":"tesco","startedAt":"2026-11-14T03:00:00Z"}""")]
    [InlineData("""{"store":"tesco","seen":-1,"startedAt":"2026-11-14T03:00:00Z"}""")]
    [InlineData("""{"store":"tesco","seen":"many","startedAt":"2026-11-14T03:00:00Z"}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    public async Task A_complete_body_that_is_not_a_sweep_is_a_400(string json)
    {
        var reader = await ReaderAsync();

        var response = await reader.PostAsync(Complete, new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_sweep_start_in_the_future_or_older_than_a_day_is_refused_so_a_skewed_clock_cannot_delist_a_shop()
    {
        var reader = await ReaderAsync();
        await PostAsync(reader, "tesco", Tesco(1));
        var now = _factory.Clock.GetUtcNow();

        var future = await CompleteAsync(reader, "tesco", 1, now.AddHours(2));
        var ancient = await CompleteAsync(reader, "tesco", 1, now.AddDays(-2));

        future.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ancient.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.WithDb(db => db.PricePullRuns.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData("lidl", "store_not_ingested")]
    [InlineData("superquinn", "unknown_store")]
    public async Task A_sweep_of_a_shop_the_server_reads_or_does_not_know_is_a_422(string store, string error)
    {
        var reader = await ReaderAsync();

        var response = await CompleteAsync(reader, store, 1, _factory.Clock.GetUtcNow().AddMinutes(-5));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be(error);
        (await _factory.WithDb(db => db.PricePullRuns.CountAsync())).Should().Be(0);
    }
}
