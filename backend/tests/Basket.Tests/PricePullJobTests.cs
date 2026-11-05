using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Sources;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Basket.Tests;

/// <summary>The nightly job against a real database and fake shop sources (so no network, and each shop can be made to fail).</summary>
public sealed class PricePullJobTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public PricePullJobTests(BasketFactory factory) => _factory = factory;

    private sealed class FakeSource(Store store, Func<IReadOnlyList<ListingToCheck>, StorePull> pull) : IStorePriceSource
    {
        public Store Store => store;

        public int Calls { get; private set; }

        public Task<StorePull> PullAsync(IReadOnlyList<ListingToCheck> listings, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(pull(listings));
        }
    }

    private static PriceQuote Quote(decimal price, decimal? loyalty = null, string? promo = null) =>
        new("Shop title", price, null, loyalty, loyalty is null ? null : "Lidl Plus", promo, null, null, true);

    private static StorePull Priced(IReadOnlyList<ListingToCheck> listings, Func<ListingToCheck, PriceQuote> quote) =>
        new(PullOutcome.Ok, null, listings.ToDictionary(l => l.ListingId, l => ListingPull.Priced(quote(l))));

    private async Task<Guid> SeedAsync(params (string Product, Store Store)[] links)
    {
        var (_, _) = await _factory.OwnerAsync();
        return await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            var owner = await db.Users.Select(u => u.Id).FirstAsync();
            var products = new Dictionary<string, Product>();
            foreach (var (name, store) in links)
            {
                if (!products.TryGetValue(name, out var product))
                {
                    product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = _factory.Clock.GetUtcNow(), AddedByUserId = owner };
                    products[name] = product;
                    db.Products.Add(product);
                }

                db.StoreListings.Add(new StoreListing
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Store = store,
                    Url = store switch
                    {
                        Store.Lidl => $"https://www.lidl.ie/p/{name}/p1100{products.Count}",
                        Store.Aldi => $"https://www.aldi.ie/product/{name}-00000000000042254{products.Count}",
                        _ => $"https://www.tesco.ie/groceries/en-IE/products/25431739{products.Count}",
                    },
                    AddedAt = _factory.Clock.GetUtcNow(),
                });
            }

            await db.SaveChangesAsync();
            return owner;
        });
    }

    private FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-05T03:00:00Z"));

    private PricePullJob Job(params IStorePriceSource[] sources) =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), sources, _clock, NullLogger<PricePullJob>.Instance);

    /// <summary>The job's own clock, so a test can move it to any day (the factory's can only go forward).</summary>
    private void At(string utc) => _clock = new FakeTimeProvider(DateTimeOffset.Parse(utc));

    [Fact]
    public async Task Each_shop_is_read_and_stored_with_the_date_and_a_run_per_shop()
    {
        await SeedAsync(("milk", Store.Lidl), ("milk", Store.Aldi));
        At("2026-11-05T03:00:00Z");
        var lidl = new FakeSource(Store.Lidl, l => Priced(l, _ => Quote(1.29m, loyalty: 0.99m, promo: "Lidl Plus deal")));
        var aldi = new FakeSource(Store.Aldi, l => Priced(l, _ => Quote(1.19m)));

        await Job(lidl, aldi).RunAsync(CancellationToken.None);

        await _factory.WithDb(async db =>
        {
            var observations = await db.PriceObservations.ToListAsync();
            observations.Should().HaveCount(2);
            observations.Should().OnlyContain(o => o.ObservedOn == new DateOnly(2026, 11, 5));
            var lidlObservation = observations.Single(o => o.LoyaltyPrice != null);
            lidlObservation.Price.Should().Be(1.29m);
            lidlObservation.LoyaltyPrice.Should().Be(0.99m);
            lidlObservation.PromoText.Should().Be("Lidl Plus deal");
            var runs = await db.PricePullRuns.ToListAsync();
            runs.Should().HaveCount(2);
            runs.Should().OnlyContain(r => r.Outcome == PullOutcome.Ok && r.Listings == 1 && r.Read == 1);
            (await db.StoreListings.ToListAsync()).Should().OnlyContain(l => l.LastError == null && l.Title == "Shop title" && l.LastCheckedAt != null);
            return 0;
        });
    }

    [Fact]
    public async Task A_shop_that_throws_does_not_lose_the_others()
    {
        await SeedAsync(("milk", Store.Lidl), ("milk", Store.Aldi), ("milk", Store.Tesco));
        At("2026-11-05T03:00:00Z");
        var lidl = new FakeSource(Store.Lidl, _ => throw new InvalidOperationException("lidl exploded"));
        var aldi = new FakeSource(Store.Aldi, l => Priced(l, _ => Quote(1.19m)));
        var tesco = new FakeSource(Store.Tesco, l => Priced(l, _ => Quote(1.35m)));

        await Job(lidl, aldi, tesco).RunAsync(CancellationToken.None);

        await _factory.WithDb(async db =>
        {
            var observations = await db.PriceObservations.Join(db.StoreListings, o => o.ListingId, l => l.Id, (o, l) => new { l.Store, o.Price }).ToListAsync();
            observations.Should().BeEquivalentTo([new { Store = Store.Aldi, Price = (decimal?)1.19m }, new { Store = Store.Tesco, Price = (decimal?)1.35m }]);
            var failed = await db.PricePullRuns.SingleAsync(r => r.Store == Store.Lidl);
            failed.Outcome.Should().Be(PullOutcome.Failed);
            failed.Message.Should().Contain("lidl exploded");
            (await db.PricePullRuns.CountAsync(r => r.Outcome == PullOutcome.Ok)).Should().Be(2);
            return 0;
        });
    }

    [Fact]
    public async Task A_blocked_shop_is_recorded_as_blocked_with_no_prices_and_the_others_still_run()
    {
        await SeedAsync(("milk", Store.Tesco), ("milk", Store.Aldi));
        At("2026-11-05T03:00:00Z");
        var tesco = new FakeSource(Store.Tesco, l => new StorePull(
            PullOutcome.Blocked,
            "www.tesco.ie answered HTTP 403. basket does not try to get around a block.",
            l.ToDictionary(x => x.ListingId, _ => ListingPull.Failed("blocked"))));
        var aldi = new FakeSource(Store.Aldi, l => Priced(l, _ => Quote(1.19m)));

        await Job(tesco, aldi).RunAsync(CancellationToken.None);

        await _factory.WithDb(async db =>
        {
            var run = await db.PricePullRuns.SingleAsync(r => r.Store == Store.Tesco);
            run.Outcome.Should().Be(PullOutcome.Blocked);
            run.Read.Should().Be(0);
            (await db.StoreListings.SingleAsync(l => l.Store == Store.Tesco)).LastError.Should().Be("blocked");
            (await db.PriceObservations.CountAsync()).Should().Be(1);
            return 0;
        });
    }

    [Fact]
    public async Task Running_twice_in_one_day_keeps_one_reading_and_the_next_day_adds_another()
    {
        await SeedAsync(("milk", Store.Aldi));
        var price = 1.19m;
        var aldi = new FakeSource(Store.Aldi, l => Priced(l, _ => Quote(price)));

        At("2026-11-05T03:00:00Z");
        await Job(aldi).RunAsync(CancellationToken.None);
        price = 1.25m;
        At("2026-11-05T12:00:00Z");
        await Job(aldi).RunAsync(CancellationToken.None);
        price = 1.09m;
        At("2026-11-06T03:00:00Z");
        await Job(aldi).RunAsync(CancellationToken.None);

        await _factory.WithDb(async db =>
        {
            var history = await db.PriceObservations.OrderBy(o => o.ObservedOn).ToListAsync();
            history.Select(o => (o.ObservedOn, o.Price)).Should().Equal(
                (new DateOnly(2026, 11, 5), (decimal?)1.25m),
                (new DateOnly(2026, 11, 6), (decimal?)1.09m));
            return 0;
        });
    }

    [Fact]
    public async Task The_dublin_date_decides_which_day_a_late_night_reading_belongs_to()
    {
        await SeedAsync(("milk", Store.Aldi));
        var aldi = new FakeSource(Store.Aldi, l => Priced(l, _ => Quote(1.19m)));
        // 23:30 UTC in summer is 00:30 the next day in Dublin.
        At("2026-07-01T23:30:00Z");

        await Job(aldi).RunAsync(CancellationToken.None);

        await _factory.WithDb(async db =>
        {
            (await db.PriceObservations.SingleAsync()).ObservedOn.Should().Be(new DateOnly(2026, 7, 2));
            return 0;
        });
    }

    [Fact]
    public async Task A_product_page_that_stops_parsing_keeps_the_last_known_price_and_says_why()
    {
        await SeedAsync(("milk", Store.Aldi), ("bread", Store.Aldi));
        var broken = false;
        var aldi = new FakeSource(Store.Aldi, l => new StorePull(
            PullOutcome.Ok,
            null,
            l.ToDictionary(
                x => x.ListingId,
                x => broken && x.Url.ToString().Contains("milk") ? ListingPull.Failed("The Aldi page has no product data.") : ListingPull.Priced(Quote(1.19m)))));

        At("2026-11-05T03:00:00Z");
        await Job(aldi).RunAsync(CancellationToken.None);
        broken = true;
        At("2026-11-06T03:00:00Z");
        await Job(aldi).RunAsync(CancellationToken.None);

        await _factory.WithDb(async db =>
        {
            var milk = await db.StoreListings.SingleAsync(l => l.Url.Contains("milk"));
            milk.LastError.Should().Contain("no product data");
            (await db.PriceObservations.CountAsync(o => o.ListingId == milk.Id)).Should().Be(1, "yesterday's reading stays");
            (await db.PriceObservations.CountAsync()).Should().Be(3);
            return 0;
        });
    }

    [Fact]
    public async Task A_shop_with_nothing_linked_is_not_asked_at_all()
    {
        await SeedAsync(("milk", Store.Aldi));
        var lidl = new FakeSource(Store.Lidl, l => Priced(l, _ => Quote(1m)));
        var aldi = new FakeSource(Store.Aldi, l => Priced(l, _ => Quote(1.19m)));

        await Job(lidl, aldi).RunAsync(CancellationToken.None);

        lidl.Calls.Should().Be(0);
        aldi.Calls.Should().Be(1);
        await _factory.WithDb(async db =>
        {
            (await db.PricePullRuns.AnyAsync(r => r.Store == Store.Lidl)).Should().BeFalse();
            return 0;
        });
    }
}
