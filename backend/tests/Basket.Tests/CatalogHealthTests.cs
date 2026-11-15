using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The per-shop catalog health the Prices page shows: what the pure rules say, and what <c>GET /api/products</c> reports from the tables.</summary>
public sealed class CatalogHealthTests : IClassFixture<BasketFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");

    private readonly BasketFactory _factory;

    public CatalogHealthTests(BasketFactory factory) => _factory = factory;

    private static CatalogHealth.SweepRun Sweep(Store store, double daysAgo, PullOutcome outcome = PullOutcome.Ok, int read = 100, int live = 100) =>
        new(store, Now.AddDays(-daysAgo), outcome, read, live, null);

    private static CatalogHealthResponse Build(Store store, params CatalogHealth.SweepRun[] sweeps) =>
        CatalogHealth.Build(store, sweeps, live: 100, discontinued: 3, discontinuedOnShelf: 1, Now);

    [Fact]
    public void A_shop_with_no_sweep_row_says_never_and_guesses_nothing()
    {
        var health = Build(Store.Lidl);

        health.State.Should().Be("never");
        health.LastSweep.Should().BeNull();
        health.LastOkAt.Should().BeNull();
        health.Shrunk.Should().BeNull();
        (health.Live, health.Discontinued, health.DiscontinuedOnShelf).Should().Be((100, 3, 1), "the catalog's own counts are still true");
    }

    [Theory]
    [InlineData(Store.Lidl, 1.9, "fresh")]
    [InlineData(Store.Lidl, 2.0, "fresh")]
    [InlineData(Store.Lidl, 2.1, "stale")]
    [InlineData(Store.Aldi, 3, "stale")]
    [InlineData(Store.Tesco, 8.9, "fresh")]
    [InlineData(Store.Dunnes, 9.0, "fresh")]
    [InlineData(Store.Tesco, 9.1, "stale")]
    public void A_shop_goes_stale_after_two_days_when_the_server_sweeps_it_and_nine_when_the_pc_does(Store store, double daysAgo, string state)
    {
        var health = Build(store, Sweep(store, daysAgo));

        health.State.Should().Be(state);
        health.StaleAfterDays.Should().Be(store.ReadBy() == "pc" ? 9 : 2);
        health.LastOkAt.Should().Be(Now.AddDays(-daysAgo));
    }

    [Fact]
    public void Only_a_sweep_that_finished_ok_keeps_a_shop_fresh()
    {
        var health = Build(Store.Lidl, Sweep(Store.Lidl, 0.1, PullOutcome.Failed, read: 0), Sweep(Store.Lidl, 3));

        health.State.Should().Be("stale", "last night's failed sweep does not make the catalog fresh");
        health.LastSweep!.Outcome.Should().Be("failed");
        health.LastOkAt.Should().Be(Now.AddDays(-3));
    }

    [Fact]
    public void A_shop_whose_sweeps_never_finished_ok_is_stale_with_no_good_sweep_date()
    {
        var health = Build(Store.Aldi, Sweep(Store.Aldi, 0.5, PullOutcome.Blocked, read: 0));

        health.State.Should().Be("stale");
        health.LastOkAt.Should().BeNull();
        health.LastSweep!.Outcome.Should().Be("blocked");
    }

    [Fact]
    public void The_last_sweep_is_the_newest_whatever_order_the_rows_come_in()
    {
        var health = Build(Store.Lidl, Sweep(Store.Lidl, 3, read: 90, live: 91), Sweep(Store.Lidl, 1, read: 95, live: 96));

        (health.LastSweep!.Seen, health.LastSweep.Live).Should().Be((95, 96));
        health.LastSweep.FinishedAt.Should().Be(Now.AddDays(-1));
    }

    [Theory]
    [InlineData(89, true)]
    [InlineData(90, false)]
    [InlineData(100, false)]
    [InlineData(0, true)]
    public void A_sweep_that_listed_under_ninety_percent_of_the_last_good_one_is_flagged(int seen, bool flagged)
    {
        var health = Build(Store.Lidl, Sweep(Store.Lidl, 0.1, PullOutcome.Partial, read: seen), Sweep(Store.Lidl, 1.1, read: 100));

        (health.Shrunk is not null).Should().Be(flagged);
        if (flagged)
        {
            (health.Shrunk!.Seen, health.Shrunk.Previous).Should().Be((seen, 100));
        }
    }

    [Fact]
    public void The_baseline_is_the_last_good_sweep_not_a_partial_one_in_between()
    {
        var health = Build(
            Store.Lidl,
            Sweep(Store.Lidl, 0.1, PullOutcome.Partial, read: 55),
            Sweep(Store.Lidl, 1.1, PullOutcome.Partial, read: 50),
            Sweep(Store.Lidl, 2.1, read: 100));

        health.Shrunk.Should().NotBeNull();
        health.Shrunk!.Previous.Should().Be(100, "a half sweep is not what the next one is judged against");
    }

    [Fact]
    public void A_first_sweep_has_nothing_to_shrink_from()
    {
        Build(Store.Lidl, Sweep(Store.Lidl, 0.1, PullOutcome.Failed, read: 0)).Shrunk.Should().BeNull();
        Build(Store.Lidl, Sweep(Store.Lidl, 0.1, read: 7)).Shrunk.Should().BeNull();
    }

    private async Task<Dictionary<string, JsonElement>> StoresAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var body = await owner.GetFromJsonAsync<JsonElement>("/api/products");
        return body.GetProperty("stores").EnumerateArray().ToDictionary(s => s.GetProperty("store").GetString()!);
    }

    private async Task SeedAsync(Action<AppDbContext> seed) =>
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            seed(db);
            await db.SaveChangesAsync();
            return 0;
        });

    private StoreProduct Catalog(Store store, string id, DateTimeOffset? discontinuedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Store = store,
        ExternalId = id,
        Url = $"https://{store.Host()}/p/{id}",
        Name = $"Invented product {id}",
        FirstSeenAt = _factory.Clock.GetUtcNow().AddDays(-30),
        LastSeenAt = _factory.Clock.GetUtcNow(),
        DiscontinuedAt = discontinuedAt,
    };

    private PricePullRun Run(Store store, RunKind? kind, double daysAgo, PullOutcome outcome = PullOutcome.Ok, int read = 10, int listings = 10) => new()
    {
        Id = Guid.NewGuid(),
        Store = store,
        Kind = kind,
        StartedAt = _factory.Clock.GetUtcNow().AddDays(-daysAgo).AddMinutes(-5),
        FinishedAt = _factory.Clock.GetUtcNow().AddDays(-daysAgo),
        Outcome = outcome,
        Read = read,
        Listings = listings,
    };

    [Fact]
    public async Task The_products_response_carries_each_shops_catalog_health_from_its_sweep_rows()
    {
        await SeedAsync(db =>
        {
            db.StoreProducts.AddRange(Catalog(Store.Lidl, "a1"), Catalog(Store.Lidl, "a2"), Catalog(Store.Lidl, "a3", _factory.Clock.GetUtcNow().AddDays(-1)), Catalog(Store.Tesco, "t1"));
            db.PricePullRuns.AddRange(
                Run(Store.Lidl, RunKind.Sweep, 1, read: 100, listings: 100),
                Run(Store.Lidl, RunKind.Sweep, 0.1, PullOutcome.Partial, read: 40, listings: 100),
                Run(Store.Aldi, RunKind.Sweep, 5, read: 50, listings: 50),
                Run(Store.Tesco, RunKind.Shelf, 0.1),
                Run(Store.Dunnes, null, 0.1));
        });

        var stores = await StoresAsync();

        var lidl = stores["lidl"].GetProperty("catalog");
        lidl.GetProperty("state").GetString().Should().Be("fresh", "the Ok sweep of yesterday is within two days");
        lidl.GetProperty("staleAfterDays").GetInt32().Should().Be(2);
        lidl.GetProperty("lastSweep").GetProperty("outcome").GetString().Should().Be("partial");
        lidl.GetProperty("lastSweep").GetProperty("seen").GetInt32().Should().Be(40);
        lidl.GetProperty("lastSweep").GetProperty("live").GetInt32().Should().Be(100);
        lidl.GetProperty("live").GetInt32().Should().Be(2);
        lidl.GetProperty("discontinued").GetInt32().Should().Be(1);
        lidl.GetProperty("shrunk").GetProperty("previous").GetInt32().Should().Be(100);

        stores["aldi"].GetProperty("catalog").GetProperty("state").GetString().Should().Be("stale");
        stores["tesco"].GetProperty("catalog").GetProperty("state").GetString().Should().Be("never", "a shelf run is not a sweep");
        stores["tesco"].GetProperty("catalog").GetProperty("live").GetInt32().Should().Be(1);
        stores["dunnes"].GetProperty("catalog").GetProperty("state").GetString().Should().Be("never", "a row from before the kind column has an unknown kind and is skipped");
        stores["dunnes"].GetProperty("catalog").GetProperty("lastSweep").ValueKind.Should().Be(JsonValueKind.Null);
        stores["dunnes"].GetProperty("lastRun").ValueKind.Should().NotBe(JsonValueKind.Null, "the page's existing last-run line still sees every row");
    }

    [Fact]
    public async Task A_shelf_page_the_shop_no_longer_lists_is_flagged_on_its_listing_and_counted_for_the_shop()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            return 0;
        });
        var created = await owner.PostAsJsonAsync("/api/products", new { name = "Invented butter" });
        created.EnsureSuccessStatusCode();
        var productId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var gone = Catalog(Store.Lidl, "gone", _factory.Clock.GetUtcNow().AddDays(-2));
        var live = Catalog(Store.Aldi, "live");
        await _factory.WithDb(async db =>
        {
            db.StoreProducts.AddRange(gone, live);
            db.StoreListings.AddRange(
                new StoreListing { Id = Guid.NewGuid(), ProductId = productId, Store = Store.Lidl, Url = gone.Url, AddedAt = _factory.Clock.GetUtcNow(), StoreProductId = gone.Id },
                new StoreListing { Id = Guid.NewGuid(), ProductId = productId, Store = Store.Aldi, Url = live.Url, AddedAt = _factory.Clock.GetUtcNow(), StoreProductId = live.Id });
            await db.SaveChangesAsync();
            return 0;
        });

        var body = await owner.GetFromJsonAsync<JsonElement>("/api/products");

        var entries = body.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == productId)
            .GetProperty("stores").EnumerateArray().ToDictionary(s => s.GetProperty("store").GetString()!);
        entries["lidl"].GetProperty("listing").GetProperty("discontinued").GetBoolean().Should().BeTrue();
        entries["aldi"].GetProperty("listing").GetProperty("discontinued").GetBoolean().Should().BeFalse();
        var shops = body.GetProperty("stores").EnumerateArray().ToDictionary(s => s.GetProperty("store").GetString()!);
        shops["lidl"].GetProperty("catalog").GetProperty("discontinuedOnShelf").GetInt32().Should().Be(1);
        shops["aldi"].GetProperty("catalog").GetProperty("discontinuedOnShelf").GetInt32().Should().Be(0);
    }
}
