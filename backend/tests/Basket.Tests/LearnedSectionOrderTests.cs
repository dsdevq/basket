using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Plan;
using Basket.Api.Prices;
using Basket.Api.Walk;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>A shop's walk order learned from the order lines were ticked across closed trips, tested on recorded trip sequences.</summary>
public sealed class LearnedSectionOrderTests
{
    private const string Fruit = "Fruit & veg";
    private const string Bakery = "Bakery";
    private const string Meat = "Meat & fish";
    private const string Dairy = "Dairy & eggs";
    private const string Frozen = "Frozen";
    private const string Cupboard = "Cupboard";
    private const string Drinks = "Drinks";

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One recorded trip at <paramref name="store"/>: each section is ticked twice, at the given minutes after the trip started, and
    /// the trip is <paramref name="day"/> days after the first of November (so a later day is a later trip).
    /// </summary>
    private static List<WalkTick> Trip(int day, Store store, params (string Section, int Minute)[] walk) =>
        TripOf(Guid.NewGuid(), day, store, walk);

    private static List<WalkTick> TripOf(Guid id, int day, Store store, (string Section, int Minute)[] walk) =>
        walk.SelectMany(w => new[]
        {
            new WalkTick(id, store, w.Section, Start.AddDays(day).AddMinutes(w.Minute)),
            new WalkTick(id, store, w.Section, Start.AddDays(day).AddMinutes(w.Minute).AddSeconds(40)),
        }).ToList();

    private static LearnedOrder? Learn(Store store, params List<WalkTick>[] trips) => LearnedSectionOrder.Learn(store, trips.SelectMany(t => t));

    /// <summary>The shop the owner described is walked drinks-first: three such trips in a row.</summary>
    private static readonly (string, int)[] DrinksFirst = [(Drinks, 0), (Cupboard, 5), (Fruit, 10), (Bakery, 15), (Meat, 20), (Dairy, 25)];

    [Fact]
    public void Three_trips_that_show_a_walk_replace_the_seeded_order_and_the_unseen_sections_follow_in_seeded_order()
    {
        var learned = Learn(Store.Lidl, Trip(1, Store.Lidl, DrinksFirst), Trip(8, Store.Lidl, DrinksFirst), Trip(15, Store.Lidl, DrinksFirst));

        learned.Should().NotBeNull();
        learned!.Trips.Should().Be(3);
        learned.Sections.Should().Equal(Drinks, Cupboard, Fruit, Bakery, Meat, Dairy, Frozen, "Household", "Health & beauty", "Pet", SeededSectionOrder.Other);
    }

    [Fact]
    public void Two_trips_are_not_enough_and_the_shop_stays_seeded()
    {
        Learn(Store.Lidl, Trip(1, Store.Lidl, DrinksFirst), Trip(8, Store.Lidl, DrinksFirst)).Should().BeNull();
    }

    [Fact]
    public void A_list_ticked_off_in_one_go_shows_no_walk_and_does_not_count()
    {
        (string, int)[] sofa = [(Dairy, 0), (Meat, 0), (Fruit, 1), (Drinks, 1)];

        Learn(Store.Lidl, Trip(1, Store.Lidl, DrinksFirst), Trip(8, Store.Lidl, DrinksFirst), Trip(15, Store.Lidl, sofa)).Should().BeNull("one of the three ticked everything inside three minutes");
    }

    [Fact]
    public void A_trip_with_too_few_lines_or_sections_does_not_count()
    {
        (string, int)[] twoSections = [(Fruit, 0), (Dairy, 10), (Fruit, 20), (Dairy, 30)];
        (string, int)[] shortTrip = [(Fruit, 0), (Dairy, 10)];

        Learn(Store.Lidl, Trip(1, Store.Lidl, DrinksFirst), Trip(8, Store.Lidl, DrinksFirst), Trip(15, Store.Lidl, twoSections), Trip(22, Store.Lidl, shortTrip)).Should().BeNull();
    }

    [Fact]
    public void Lines_bought_at_another_shop_and_lines_in_other_are_left_out()
    {
        var mixed = Trip(1, Store.Lidl, DrinksFirst);
        mixed.AddRange(Trip(1, Store.Aldi, DrinksFirst).Select(t => t with { TripId = mixed[0].TripId }));
        mixed.AddRange(Trip(1, Store.Lidl, (SeededSectionOrder.Other, 1)).Select(t => t with { TripId = mixed[0].TripId }));

        var learned = Learn(Store.Lidl, mixed, Trip(8, Store.Lidl, DrinksFirst), Trip(15, Store.Lidl, DrinksFirst));

        learned!.Sections.Should().StartWith(Drinks, Cupboard).And.EndWith(SeededSectionOrder.Other);
        learned.Sections.Count(s => s == SeededSectionOrder.Other).Should().Be(1);
        Learn(Store.Aldi, mixed).Should().BeNull("one Aldi walk is one trip, not three");
    }

    [Fact]
    public void One_odd_trip_among_the_others_does_not_flip_the_order()
    {
        (string, int)[] odd = [(Dairy, 0), (Meat, 5), (Bakery, 10), (Fruit, 15), (Cupboard, 20), (Drinks, 25)];

        var learned = Learn(Store.Lidl, Trip(1, Store.Lidl, DrinksFirst), Trip(8, Store.Lidl, DrinksFirst), Trip(15, Store.Lidl, odd), Trip(22, Store.Lidl, DrinksFirst));

        learned!.Sections.Take(6).Should().Equal(Drinks, Cupboard, Fruit, Bakery, Meat, Dairy);
    }

    [Fact]
    public void Trips_that_each_visit_some_sections_still_make_one_order()
    {
        var a = Trip(1, Store.Lidl, (Drinks, 0), (Fruit, 10), (Dairy, 20));
        var b = Trip(8, Store.Lidl, (Fruit, 0), (Meat, 10), (Dairy, 20));
        var c = Trip(15, Store.Lidl, (Drinks, 0), (Meat, 10), (Dairy, 20));

        Learn(Store.Lidl, a, b, c)!.Sections.Take(4).Should().Equal(Drinks, Fruit, Meat, Dairy);
    }

    [Fact]
    public void A_section_seen_on_one_trip_only_is_not_placed_by_the_learning()
    {
        (string, int)[] withFrozen = [(Frozen, 0), .. DrinksFirst.Select(w => (w.Item1, w.Item2 + 5))];

        var learned = Learn(Store.Lidl, Trip(1, Store.Lidl, withFrozen), Trip(8, Store.Lidl, DrinksFirst), Trip(15, Store.Lidl, DrinksFirst));

        learned!.Sections.Take(6).Should().Equal(Drinks, Cupboard, Fruit, Bakery, Meat, Dairy);
        learned.Sections[6].Should().Be(Frozen, "it keeps its seeded place after what was learned");
    }

    [Fact]
    public void Only_the_latest_ten_trips_count_because_shops_move_their_shelves()
    {
        (string, int)[] oldWalk = [(Fruit, 0), (Bakery, 5), (Meat, 10), (Dairy, 15), (Frozen, 20), (Cupboard, 25)];
        var trips = Enumerable.Range(1, 10).Select(d => Trip(d, Store.Lidl, oldWalk))
            .Concat(Enumerable.Range(20, 10).Select(d => Trip(d, Store.Lidl, DrinksFirst)))
            .ToArray();

        Learn(Store.Lidl, trips)!.Sections.Take(3).Should().Equal(Drinks, Cupboard, Fruit);
    }

    [Fact]
    public void Each_shop_learns_on_its_own_and_the_others_stay_seeded()
    {
        var ticks = new[] { 1, 8, 15 }.SelectMany(d => Trip(d, Store.Lidl, DrinksFirst)).ToList();

        var walk = WalkOrders.From(ticks);

        walk.IsLearned(Store.Lidl).Should().BeTrue();
        walk.TripsOf(Store.Lidl).Should().Be(3);
        walk.IsLearned(Store.Aldi).Should().BeFalse();
        walk.For(Store.Aldi).Should().Equal(SeededSectionOrder.For(Store.Aldi));
        walk.RankOfSection(Store.Lidl, Drinks).Should().Be(0);
        walk.RankOfSection(Store.Aldi, Drinks).Should().Be(SeededSectionOrder.RankOfSection(Store.Aldi, Drinks));
        walk.RankOfSection(Store.Lidl, "no such section").Should().Be(walk.For(Store.Lidl).Count - 1);
    }

    [Fact]
    public async Task The_basket_and_the_walk_endpoint_follow_the_order_learned_from_closed_trips()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var today = PriceRecorder.DublinDay(factory.Clock.GetUtcNow());
        var now = factory.Clock.GetUtcNow();
        (string Name, string Category)[] shelf =
        [
            ("Apples", "Food & Drink > Fruit & Vegetables > Fruit"),
            ("Bread", "Food & Drink > Bakery"),
            ("Chicken fillets", "Food & Drink > Meat & Poultry"),
            ("Cola 2L", "Food & Drink > Drinks"),
        ];
        await factory.WithDb(async db =>
        {
            var by = db.Users.Select(u => u.Id).First();
            var products = shelf.Select(s => new Product { Id = Guid.NewGuid(), Name = s.Name, AddedAt = now, AddedByUserId = by }).ToList();
            db.Products.AddRange(products);
            foreach (var (product, (_, category)) in products.Zip(shelf))
            {
                var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Lidl, Url = $"https://www.lidl.ie/p/{product.Id:N}/p1", Category = category, AddedAt = now };
                db.StoreListings.Add(listing);
                db.PriceObservations.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = today, ObservedAt = now, Price = 1.00m, InStock = true });
                db.ListItems.Add(new ListItem { Id = Guid.NewGuid(), Text = product.Name, ProductId = product.Id, AddedAt = now.AddSeconds(db.ListItems.Local.Count), AddedByUserId = by });
            }

            // Three shops of the Lidl on the recorded walk: cola, bread, chicken, apples, four minutes apart.
            foreach (var week in new[] { 1, 2, 3 })
            {
                var trip = new Trip { Id = Guid.NewGuid(), ClosedAt = now.AddDays(-7 * week) };
                db.Trips.Add(trip);
                foreach (var (name, minute) in new[] { ("Cola 2L", 0), ("Bread", 4), ("Chicken fillets", 8), ("Apples", 12) })
                {
                    db.TripItems.Add(new TripItem
                    {
                        Id = Guid.NewGuid(), TripId = trip.Id, Text = name, ProductId = products.Single(p => p.Name == name).Id,
                        BoughtStore = Store.Lidl, CheckedAt = trip.ClosedAt!.Value.AddMinutes(minute - 30),
                    });
                }
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var order = await owner.GetFromJsonAsync<JsonElement>("/api/walk/order");
        var lidl = order.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "lidl");
        lidl.GetProperty("source").GetString().Should().Be("learned");
        lidl.GetProperty("trips").GetInt32().Should().Be(3);
        lidl.GetProperty("sections").EnumerateArray().Select(s => s.GetString()).Take(4).Should().Equal(Drinks, Bakery, Meat, Fruit);
        order.GetProperty("stores").EnumerateArray().Where(s => s.GetProperty("store").GetString() != "lidl")
            .Should().OnlyContain(s => s.GetProperty("source").GetString() == "seeded" && s.GetProperty("trips").GetInt32() == 0);

        var byShop = await owner.GetFromJsonAsync<JsonElement>("/api/plan/by-shop");
        byShop.GetProperty("split").GetProperty("groups")[0].GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("text").GetString()).Should().Equal("Cola 2L", "Bread", "Chicken fillets", "Apples");
    }
}
