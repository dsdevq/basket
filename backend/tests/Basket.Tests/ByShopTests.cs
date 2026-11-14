using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Plan;
using Basket.Api.Prices;
using Basket.Api.Walk;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The basket priced at each shop: a total per shop, what each lacks, the cheapest split, and nothing guessed.</summary>
public sealed class ByShopTests : IClassFixture<BasketFactory>
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private readonly BasketFactory _factory;

    public ByShopTests(BasketFactory factory) => _factory = factory;

    private sealed class Shelf
    {
        private readonly List<Product> _products = [];
        private readonly List<StoreListing> _listings = [];
        private readonly Dictionary<Guid, PriceObservation> _latest = [];
        private readonly List<ListItem> _lines = [];

        public ListItem Line(string name, params (Store Store, decimal Price, string Title)[] shops) =>
            Line(name, shops.Select(s => (s.Store, (Action<PriceObservation>)(o => o.Price = s.Price), s.Title, (DateOnly?)null)).ToArray());

        public ListItem Line(string name, params (Store Store, Action<PriceObservation> Reading, string Title, DateOnly? On)[] shops)
        {
            var product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = DateTimeOffset.UtcNow };
            _products.Add(product);
            foreach (var (store, reading, title, on) in shops)
            {
                var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = store, Url = "https://example.test/" + Guid.NewGuid(), Title = title };
                _listings.Add(listing);
                var observation = new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = on ?? Today, InStock = true };
                reading(observation);
                _latest[listing.Id] = observation;
            }

            var line = new ListItem { Id = Guid.NewGuid(), Text = name, ProductId = product.Id, AddedAt = DateTimeOffset.UtcNow.AddSeconds(_lines.Count) };
            _lines.Add(line);
            return line;
        }

        /// <summary>Files the line's page at <paramref name="store"/> under a shop breadcrumb, as the price job reads it.</summary>
        public void Categorise(ListItem line, Store store, string category) =>
            _listings.Single(l => l.ProductId == line.ProductId && l.Store == store).Category = category;

        public ListItem FreeText(string text)
        {
            var line = new ListItem { Id = Guid.NewGuid(), Text = text, AddedAt = DateTimeOffset.UtcNow.AddSeconds(_lines.Count) };
            _lines.Add(line);
            return line;
        }

        public ByShopResponse Build() =>
            ByShopBuilder.Build(PlanBuilder.Build(_lines, _products.ToDictionary(p => p.Id), _listings.ToLookup(l => l.ProductId), _latest, Today));
    }

    private static ByShopStore At(ByShopResponse response, string store) => response.Stores.Single(s => s.Store == store);

    [Fact]
    public void Every_shop_gets_a_total_of_the_lines_it_prices_and_the_cheapest_complete_one_is_marked()
    {
        var shelf = new Shelf();
        shelf.Line("Milk", (Store.Lidl, 1.20m, "Milk 1L"), (Store.Aldi, 1.10m, "Milk 1L"), (Store.Tesco, 1.30m, "Milk 1L"));
        shelf.Line("Butter", (Store.Lidl, 2.50m, "Butter 250g"), (Store.Aldi, 2.40m, "Butter 250g"), (Store.Tesco, 2.60m, "Butter 250g"));

        var response = shelf.Build();

        response.LineCount.Should().Be(2);
        response.Stores.Select(s => s.Store).Should().Equal("lidl", "aldi", "tesco", "dunnes");
        At(response, "lidl").Total.Should().Be(3.70m);
        At(response, "aldi").Total.Should().Be(3.50m);
        At(response, "tesco").Total.Should().Be(3.90m);
        response.Stores.Where(s => s.Cheapest).Select(s => s.Store).Should().Equal("aldi");
        At(response, "aldi").Complete.Should().BeTrue();
        At(response, "aldi").OldestSeenOn.Should().Be(Today);
    }

    [Fact]
    public void A_shop_that_lacks_a_line_lists_it_with_the_reason_and_is_never_totalled_as_if_it_had_it()
    {
        var shelf = new Shelf();
        var milk = shelf.Line("Milk", (Store.Lidl, 1.20m, "Milk 1L"), (Store.Aldi, 1.10m, "Milk 1L"));
        var eggs = shelf.Line("Eggs", (Store.Lidl, 2.00m, "Eggs 12"));
        var oats = shelf.Line(
            "Oats",
            (Store.Lidl, o => o.Price = 1.00m, "Oats 1kg", null),
            (Store.Aldi, o => o.Price = 0.90m, "Oats 1kg", Today.AddDays(-9)),
            (Store.Dunnes, o => { o.Price = 0.95m; o.InStock = false; }, "Oats 1kg", null));
        var candles = shelf.FreeText("candles");

        var response = shelf.Build();

        var aldi = At(response, "aldi");
        aldi.Total.Should().Be(1.10m);
        aldi.Priced.Should().Be(1);
        aldi.Complete.Should().BeFalse();
        aldi.Cheapest.Should().BeFalse();
        aldi.Missing.Should().Equal(
            new PlanBlocker(eggs.Id, "Eggs", PlanStoreStates.NotLinked),
            new PlanBlocker(oats.Id, "Oats", PlanStoreStates.Stale),
            new PlanBlocker(candles.Id, "candles", PlanFlags.Unmatched));
        At(response, "dunnes").Missing.Select(m => (m.ItemId, m.Reason)).Should().Contain((oats.Id, PlanStoreStates.OutOfStock));
        At(response, "tesco").Priced.Should().Be(0);
        At(response, "tesco").Total.Should().Be(0m);
        At(response, "lidl").Missing.Should().ContainSingle().Which.ItemId.Should().Be(candles.Id);
        response.Stores.Should().OnlyContain(s => !s.Cheapest, "no shop prices the free-text line, so none is complete");
        milk.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Card_prices_and_promos_count_and_name_their_card()
    {
        var shelf = new Shelf();
        shelf.Line(
            "Milk",
            (Store.Tesco, o => { o.Price = 1.30m; o.LoyaltyPrice = 1.00m; o.LoyaltyScheme = "Clubcard"; }, "Milk 1L", null),
            (Store.Lidl, o => o.Price = 1.20m, "Milk 1L", null));
        shelf.Line("Yoghurt", (Store.Tesco, o => { o.Price = 3.00m; o.PromoText = "2 for €4"; }, "Yoghurt 500g", null));

        var tesco = At(shelf.Build(), "tesco");

        tesco.Total.Should().Be(3.00m, "1.00 on the card price and 2.00 for one of two for four euro");
        tesco.Cards.Should().Equal("Clubcard");
        tesco.PromoLines.Should().Be(2);
    }

    [Fact]
    public void Packs_that_differ_between_shops_are_counted_so_the_totals_are_not_read_as_like_for_like()
    {
        var shelf = new Shelf();
        shelf.Line("Milk", (Store.Lidl, 1.20m, "Milk 1L"), (Store.Aldi, 2.20m, "Milk 2L"));
        shelf.Line("Butter", (Store.Lidl, 2.50m, "Butter 250g"), (Store.Aldi, 2.40m, "Butter 250g"));

        var response = shelf.Build();

        At(response, "lidl").PacksDiffer.Should().Be(1);
        At(response, "aldi").PacksDiffer.Should().Be(1);
        At(response, "tesco").PacksDiffer.Should().Be(0);
    }

    [Fact]
    public void The_split_buys_each_line_where_the_plan_names_it_best_and_says_how_it_compares_with_one_shop()
    {
        var shelf = new Shelf();
        shelf.Line("Milk", (Store.Lidl, 1.20m, "Milk 1L"), (Store.Aldi, 1.10m, "Milk 1L"));
        shelf.Line("Butter", (Store.Lidl, 2.30m, "Butter 250g"), (Store.Aldi, 2.40m, "Butter 250g"));

        var split = shelf.Build().Split;

        split.Complete.Should().BeTrue();
        split.Groups.Select(g => (g.Store, g.Total)).Should().Equal(("lidl", 2.30m), ("aldi", 1.10m));
        split.Groups.Single(g => g.Store == "aldi").Lines.Single().ObservedOn.Should().Be(Today);
        split.Total.Should().Be(3.40m);
        split.VersusCheapest.Should().Be(-0.10m, "the split is 3.40 and the cheapest single shop (Lidl, tied with Aldi) is 3.50");
    }

    [Fact]
    public void A_shops_lines_come_in_the_order_that_shop_is_walked_and_name_their_section()
    {
        var shelf = new Shelf();
        var cola = shelf.Line("Cola 2L", (Store.Lidl, 1.50m, "Cola 2L"));
        var chicken = shelf.Line("Chicken fillets", (Store.Lidl, 4.00m, "Chicken fillets"));
        var candles = shelf.Line("Candles", (Store.Lidl, 2.00m, "Candles"));
        var apples = shelf.Line("Apples", (Store.Lidl, 1.20m, "Apples"));
        var bananas = shelf.Line("Bananas", (Store.Lidl, 0.90m, "Bananas"));
        shelf.Categorise(cola, Store.Lidl, "Food & Drink > Drinks");
        shelf.Categorise(chicken, Store.Lidl, "Food & Drink > Meat & Poultry");
        shelf.Categorise(apples, Store.Lidl, "Food & Drink > Fruit & Vegetables > Fruit");
        shelf.Categorise(bananas, Store.Lidl, "Food & Drink > Fruit & Vegetables > Fruit");

        var lidl = shelf.Build().Split.Groups.Single();

        lidl.Lines.Select(l => (l.Text, l.Section)).Should().Equal(
            ("Apples", "Fruit & veg"),
            ("Bananas", "Fruit & veg"),
            ("Chicken fillets", "Meat & fish"),
            ("Cola 2L", "Drinks"),
            ("Candles", SeededSectionOrder.Other));
        lidl.Total.Should().Be(9.60m, "sorting the lines does not change what they cost");
    }

    [Fact]
    public void Each_chain_is_walked_in_its_own_seeded_order()
    {
        var shelf = new Shelf();
        foreach (var store in new[] { Store.Lidl, Store.Tesco })
        {
            var peas = shelf.Line($"Frozen peas {store}", (store, 1.00m, "Frozen peas"));
            var water = shelf.Line($"Spring water {store}", (store, 0.50m, "Spring water"));
            shelf.Categorise(peas, store, "Frozen Food > Peas");
            shelf.Categorise(water, store, "Drinks > Water");
        }

        var groups = shelf.Build().Split.Groups;

        groups.Single(g => g.Store == "lidl").Lines.Select(l => l.Text).Should().Equal("Frozen peas Lidl", "Spring water Lidl");
        groups.Single(g => g.Store == "tesco").Lines.Select(l => l.Text).Should().Equal("Spring water Tesco", "Frozen peas Tesco");
    }

    [Fact]
    public void A_line_the_plan_names_no_best_shop_for_is_left_out_of_the_split_with_its_flag()
    {
        var shelf = new Shelf();
        shelf.Line("Milk", (Store.Lidl, 1.20m, "Milk 1L"), (Store.Aldi, 1.10m, "Milk 1L"));
        var skewers = shelf.Line("Skewers", (Store.Lidl, 2.19m, "Skewers"), (Store.Aldi, 2.49m, "Skewers 300g"));

        var response = shelf.Build();

        response.Split.Complete.Should().BeFalse();
        response.Split.VersusCheapest.Should().BeNull("a partial split is not set against a whole basket");
        response.Split.Unplaced.Should().Equal(new PlanBlocker(skewers.Id, "Skewers", PlanFlags.NotComparable));
        response.Split.Groups.Single().Store.Should().Be("aldi");
        At(response, "lidl").Total.Should().Be(3.39m, "not_comparable lines are still priced at each shop that has a fresh price");
    }

    [Fact]
    public void An_empty_list_has_no_cheapest_shop_and_no_split()
    {
        var response = new Shelf().Build();

        response.LineCount.Should().Be(0);
        response.Stores.Should().OnlyContain(s => !s.Complete && !s.Cheapest && s.Total == 0m);
        response.Split.Complete.Should().BeFalse();
        response.Split.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task The_endpoint_sums_the_open_lines_for_a_signed_in_person_and_refuses_the_anonymous()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var today = PriceRecorder.DublinDay(_factory.Clock.GetUtcNow());
        await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var milk = new Product { Id = Guid.NewGuid(), Name = "Milk 1L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Lidl, Url = "https://www.lidl.ie/p/milk/p11000", AddedAt = DateTimeOffset.UtcNow };
            db.Products.Add(milk);
            db.StoreListings.Add(listing);
            db.ListItems.AddRange(
                new ListItem { Id = Guid.NewGuid(), Text = milk.Name, ProductId = milk.Id, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user },
                new ListItem { Id = Guid.NewGuid(), Text = "old", IsChecked = true, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user });
            db.PriceObservations.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = today, ObservedAt = _factory.Clock.GetUtcNow(), Price = 1.29m, InStock = true });
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await owner.GetFromJsonAsync<JsonElement>("/api/plan/by-shop");

        response.GetProperty("lineCount").GetInt32().Should().Be(1, "a ticked line is history");
        var lidl = response.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "lidl");
        lidl.GetProperty("total").GetDecimal().Should().Be(1.29m);
        lidl.GetProperty("cheapest").GetBoolean().Should().BeTrue();
        response.GetProperty("split").GetProperty("groups").EnumerateArray().Single().GetProperty("store").GetString().Should().Be("lidl");
        (await _factory.NewBrowser().GetAsync("/api/plan/by-shop")).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
