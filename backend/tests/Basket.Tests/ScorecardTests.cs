using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Scorecard;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>The store scorecard: wins per shop, what the winners saved over the dearest shop, and the trend, from closed trips.</summary>
public sealed class ScorecardTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    private sealed class Shelf
    {
        private readonly List<Product> _products = [];
        private readonly List<StoreListing> _listings = [];
        private readonly List<PriceObservation> _readings = [];

        public Guid Product(string name, params (Store Store, decimal Price, string Title, DateOnly On)[] shops)
        {
            var product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = DateTimeOffset.UtcNow };
            _products.Add(product);
            foreach (var (store, price, title, on) in shops)
            {
                var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = store, Url = "https://example.test/" + Guid.NewGuid(), Title = title };
                _listings.Add(listing);
                _readings.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = on, Price = price, InStock = true });
            }

            return product.Id;
        }

        public void Reading(Guid productId, Store store, decimal price, DateOnly on)
        {
            var listing = _listings.Single(l => l.ProductId == productId && l.Store == store);
            _readings.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = on, Price = price, InStock = true });
        }

        public ScorecardResponse Build(params ScorecardTrip[] trips) =>
            ScorecardBuilder.Build(trips, _products.ToDictionary(p => p.Id), _listings.ToLookup(l => l.ProductId), _readings.ToLookup(r => r.ListingId), Today);
    }

    private static ScorecardTrip Trip(DateOnly day, int lines, params Guid[] products) => new(day, lines, products);

    private static ScorecardStore At(ScorecardResponse r, string store) => r.Stores.Single(s => s.Store == store);

    [Fact]
    public void A_line_is_won_by_the_lowest_unit_price_and_saves_the_winners_pack_against_the_dearest_shop()
    {
        var day = new DateOnly(2026, 11, 10);
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 2L", (Store.Aldi, 2.00m, "Milk 2L", day), (Store.Tesco, 2.40m, "Milk 2L", day), (Store.Lidl, 2.20m, "Milk 2L", day));

        var r = shelf.Build(Trip(day, 1, milk));

        r.Compared.Should().Be(1);
        r.NotCompared.Should().Be(0);
        At(r, "aldi").Wins.Should().Be(1);
        At(r, "aldi").Share.Should().Be(100);
        r.TopStore.Should().Be("aldi");
        r.Saved.Should().Be(0.40m);
    }

    [Fact]
    public void Typed_lines_and_lines_without_two_comparable_prices_are_counted_apart_and_add_nothing()
    {
        var day = new DateOnly(2026, 11, 10);
        var shelf = new Shelf();
        var one = shelf.Product("Butter 250g", (Store.Aldi, 2.50m, "Butter 250g", day));
        var stale = shelf.Product("Eggs 12", (Store.Aldi, 3.00m, "Eggs 12", day), (Store.Lidl, 2.50m, "Eggs 12", day.AddDays(-10)));

        var r = shelf.Build(Trip(day, 3, one, stale));

        r.Compared.Should().Be(0);
        r.NotCompared.Should().Be(3);
        r.Saved.Should().Be(0);
        r.TopStore.Should().BeNull();
        r.Stores.Should().OnlyContain(s => s.Wins == 0 && s.Share == 0);
    }

    [Fact]
    public void Prices_are_read_as_they_were_on_the_day_of_the_trip_not_today()
    {
        var shelf = new Shelf();
        var tripDay = new DateOnly(2026, 11, 3);
        // Aldi was cheaper on the trip day; a later reading (today) has Lidl cheaper and must not change the trip's winner.
        var milk = shelf.Product("Milk 2L", (Store.Aldi, 1.90m, "Milk 2L", tripDay), (Store.Lidl, 2.20m, "Milk 2L", tripDay));
        shelf.Reading(milk, Store.Lidl, 1.50m, Today);

        var r = shelf.Build(Trip(tripDay, 1, milk));

        At(r, "aldi").Wins.Should().Be(1);
        At(r, "lidl").Wins.Should().Be(0);
    }

    [Fact]
    public void Equal_prices_go_to_the_first_shop_in_the_fixed_order()
    {
        var day = new DateOnly(2026, 11, 10);
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 2L", (Store.Tesco, 2.00m, "Milk 2L", day), (Store.Lidl, 2.00m, "Milk 2L", day));

        var r = shelf.Build(Trip(day, 1, milk));

        At(r, "lidl").Wins.Should().Be(1);
        r.Saved.Should().Be(0);
    }

    [Fact]
    public void The_trend_has_one_row_per_month_of_the_window_and_trips_before_it_are_left_out()
    {
        var shelf = new Shelf();
        var oct = new DateOnly(2026, 10, 20);
        var nov = new DateOnly(2026, 11, 10);
        var old = new DateOnly(2026, 4, 30);
        var milk = shelf.Product("Milk 2L", (Store.Aldi, 2.00m, "Milk 2L", oct), (Store.Lidl, 2.50m, "Milk 2L", oct), (Store.Aldi, 2.00m, "Milk 2L", nov));

        var r = shelf.Build(Trip(oct, 1, milk), Trip(nov, 1), Trip(old, 1, milk));

        r.Trend.Select(m => m.Month).Should().Equal("2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11");
        r.Trips.Should().Be(2);
        r.Trend.Single(m => m.Month == "2026-10").Should().Be(new ScorecardMonth("2026-10", 1, 1, 0.50m));
        r.Trend.Single(m => m.Month == "2026-11").Should().Be(new ScorecardMonth("2026-11", 1, 0, 0m));
        r.Trend.Where(m => m.Month is not ("2026-10" or "2026-11")).Should().OnlyContain(m => m.Trips == 0);
    }

    [Fact]
    public async Task The_endpoint_scores_a_closed_trip_from_the_daily_prices()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var day = PriceRecorder.DublinDay(factory.Clock.GetUtcNow());
        await factory.WithDb(async db =>
        {
            var by = db.Users.Select(u => u.Id).First();
            var milk = new Product { Id = Guid.NewGuid(), Name = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(milk);
            foreach (var (store, price) in new[] { (Store.Aldi, 2.00m), (Store.Tesco, 2.40m) })
            {
                var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = store, Url = "https://example.test/" + store, Title = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow };
                db.StoreListings.Add(listing);
                db.PriceObservations.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = day, Price = price, InStock = true });
            }

            var trip = new Trip { Id = Guid.NewGuid(), ClosedAt = factory.Clock.GetUtcNow() };
            db.Trips.Add(trip);
            db.TripItems.AddRange(
                new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = "Whole milk 2L", ProductId = milk.Id },
                new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = "Birthday candles" });
            await db.SaveChangesAsync();
            return 0;
        });

        var body = await owner.GetFromJsonAsync<JsonElement>("/api/scorecard");

        body.GetProperty("compared").GetInt32().Should().Be(1);
        body.GetProperty("notCompared").GetInt32().Should().Be(1);
        body.GetProperty("saved").GetDecimal().Should().Be(0.40m);
        body.GetProperty("topStore").GetString().Should().Be("aldi");
        body.GetProperty("staleAfterDays").GetInt32().Should().Be(3);
        body.GetProperty("trend").GetArrayLength().Should().Be(6);
    }
}
