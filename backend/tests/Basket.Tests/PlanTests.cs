using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Plan;
using Basket.Api.Prices;
using Basket.Api.Prices.Promotions;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The plan: the best shop per open line, whether one shop covers the list, and the lines flagged instead of guessed.</summary>
public sealed class PlanTests : IClassFixture<BasketFactory>
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private readonly BasketFactory _factory;

    public PlanTests(BasketFactory factory) => _factory = factory;

    private sealed class Shelf
    {
        private readonly List<Product> _products = [];
        private readonly List<StoreListing> _listings = [];
        private readonly Dictionary<Guid, PriceObservation> _latest = [];
        private readonly List<ListItem> _lines = [];

        public Product Product(string name)
        {
            var product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = DateTimeOffset.UtcNow };
            _products.Add(product);
            return product;
        }

        public void Read(Product product, Store store, Action<PriceObservation> reading, DateOnly? observedOn = null, string? title = null)
        {
            var listing = Link(product, store, title);
            var observation = new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = observedOn ?? Today, InStock = true };
            reading(observation);
            _latest[listing.Id] = observation;
        }

        public StoreListing Link(Product product, Store store, string? title = null)
        {
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = store, Url = "https://example.test/" + Guid.NewGuid(), Title = title };
            _listings.Add(listing);
            return listing;
        }

        public ListItem Line(Product product, Store? picked = null)
        {
            var line = new ListItem { Id = Guid.NewGuid(), Text = product.Name, ProductId = product.Id, Store = picked, AddedAt = DateTimeOffset.UtcNow.AddSeconds(_lines.Count) };
            _lines.Add(line);
            return line;
        }

        public ListItem FreeText(string text)
        {
            var line = new ListItem { Id = Guid.NewGuid(), Text = text, AddedAt = DateTimeOffset.UtcNow.AddSeconds(_lines.Count) };
            _lines.Add(line);
            return line;
        }

        public PlanResponse Build() =>
            PlanBuilder.Build(_lines, _products.ToDictionary(p => p.Id), _listings.ToLookup(l => l.ProductId), _latest, Today);
    }

    private static PlanItem Of(PlanResponse plan, ListItem line) => plan.Items.Single(i => i.Id == line.Id);

    private static PlanStorePrice At(PlanItem item, string store) => item.Stores.Single(s => s.Store == store);

    // ---- best store ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_best_store_is_the_cheapest_on_the_unit_price_not_on_the_pack_price()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Whole milk");
        var line = shelf.Line(milk);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m, title: "Milk 1L"); // 1.20/l
        shelf.Read(milk, Store.Aldi, o => o.Price = 2.20m, title: "Milk 2L"); // 1.10/l: dearer pack, cheaper litre
        shelf.Read(milk, Store.Tesco, o => { o.Price = 1.30m; o.LoyaltyPrice = 1.00m; o.LoyaltyScheme = "Clubcard"; }, title: "Milk 1L"); // card price 1.00/l

        var item = Of(shelf.Build(), line);

        item.Flag.Should().BeNull();
        item.BestStore.Should().Be("tesco");
        item.Stores.Select(s => (s.Store, s.Best)).Should().Equal(("lidl", false), ("aldi", false), ("tesco", true));
        At(item, "tesco").Offer!.Card.Should().Be("Clubcard");
        At(item, "aldi").Offer!.UnitPrice.Should().Be(1.10m);
    }

    [Fact]
    public void Each_store_carries_its_observed_date_promo_and_was_price()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        var line = shelf.Line(rice);
        shelf.Read(rice, Store.Lidl, o => { o.Price = 2.00m; o.WasPrice = 2.50m; o.PromoText = "Lidl Plus offer"; }, observedOn: Today.AddDays(-2));

        var lidl = At(Of(shelf.Build(), line), "lidl");

        lidl.State.Should().Be(PlanStoreStates.Priced);
        lidl.ObservedOn.Should().Be(Today.AddDays(-2));
        lidl.Price.Should().Be(2.00m);
        lidl.WasPrice.Should().Be(2.50m);
        lidl.PromoText.Should().Be("Lidl Plus offer");
    }

    [Fact]
    public void A_single_priced_store_is_the_best_even_without_a_pack_size()
    {
        var shelf = new Shelf();
        var skewers = shelf.Product("Chicken skewers");
        var line = shelf.Line(skewers);
        shelf.Read(skewers, Store.Lidl, o => o.Price = 2.19m);

        Of(shelf.Build(), line).BestStore.Should().Be("lidl");
    }

    [Fact]
    public void Shops_that_cannot_be_set_side_by_side_name_no_best_store()
    {
        var shelf = new Shelf();
        var skewers = shelf.Product("Chicken skewers");
        var line = shelf.Line(skewers);
        shelf.Read(skewers, Store.Lidl, o => o.Price = 2.19m); // no size anywhere
        shelf.Read(skewers, Store.Aldi, o => o.Price = 2.50m);

        var item = Of(shelf.Build(), line);

        item.Flag.Should().Be(PlanFlags.NotComparable);
        item.BestStore.Should().BeNull();
        item.Stores.Should().OnlyContain(s => s.State == PlanStoreStates.Priced && !s.Best);
    }

    [Fact]
    public void A_stale_or_out_of_stock_shop_is_shown_but_never_the_best()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        var line = shelf.Line(rice);
        shelf.Read(rice, Store.Lidl, o => o.Price = 2.00m);
        shelf.Read(rice, Store.Aldi, o => o.Price = 0.50m, observedOn: Today.AddDays(-(PromotionBuilder.StaleAfterDays + 1)));
        shelf.Read(rice, Store.Tesco, o => { o.Price = 0.40m; o.InStock = false; });

        var item = Of(shelf.Build(), line);

        item.BestStore.Should().Be("lidl");
        At(item, "aldi").State.Should().Be(PlanStoreStates.Stale);
        At(item, "aldi").ObservedOn.Should().Be(Today.AddDays(-4));
        At(item, "tesco").State.Should().Be(PlanStoreStates.OutOfStock);
    }

    // ---- flagged lines ------------------------------------------------------------------------------------------

    [Fact]
    public void A_free_text_line_is_unmatched_and_never_guessed_from_its_words()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Whole milk");
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m, title: "Milk 1L");
        var line = shelf.FreeText("whole milk");

        var item = Of(shelf.Build(), line);

        item.Flag.Should().Be(PlanFlags.Unmatched);
        item.ProductId.Should().BeNull();
        item.BestStore.Should().BeNull();
        item.Stores.Should().BeEmpty();
    }

    [Fact]
    public void A_line_whose_product_is_gone_is_unmatched()
    {
        var shelf = new Shelf();
        var line = shelf.Line(new Product { Id = Guid.NewGuid(), Name = "Deleted", AddedAt = DateTimeOffset.UtcNow });

        Of(shelf.Build(), line).Flag.Should().Be(PlanFlags.Unmatched);
    }

    [Fact]
    public void A_product_with_no_usable_price_anywhere_is_flagged_no_price()
    {
        var shelf = new Shelf();
        var unlinked = shelf.Product("Unlinked");
        var neverRead = shelf.Product("Never read");
        shelf.Link(neverRead, Store.Lidl);
        var notListed = shelf.Product("No price listed");
        shelf.Read(notListed, Store.Aldi, _ => { });
        var upcoming = shelf.Product("Starts later");
        shelf.Read(upcoming, Store.Tesco, o => { o.Price = 1.00m; o.ValidFrom = Today.AddDays(2); });
        var ended = shelf.Product("Ended");
        shelf.Read(ended, Store.Dunnes, o => { o.Price = 1.00m; o.ValidUntil = Today.AddDays(-1); });
        var lines = new[] { unlinked, neverRead, notListed, upcoming, ended }.Select(p => shelf.Line(p)).ToList();

        var plan = shelf.Build();

        lines.Should().OnlyContain(l => Of(plan, l).Flag == PlanFlags.NoPrice && Of(plan, l).BestStore == null);
        Of(plan, lines[0]).Stores.Should().BeEmpty();
        At(Of(plan, lines[1]), "lidl").State.Should().Be(PlanStoreStates.NotRead);
        At(Of(plan, lines[2]), "aldi").State.Should().Be(PlanStoreStates.NoPrice);
        At(Of(plan, lines[3]), "tesco").State.Should().Be(PlanStoreStates.Upcoming);
        At(Of(plan, lines[4]), "dunnes").State.Should().Be(PlanStoreStates.Ended);
    }

    [Fact]
    public void When_the_newest_price_is_older_than_the_window_the_line_is_stale_with_no_best_store()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        var line = shelf.Line(rice);
        shelf.Read(rice, Store.Lidl, o => o.Price = 2.00m, observedOn: Today.AddDays(-(PromotionBuilder.StaleAfterDays + 1)));
        shelf.Read(rice, Store.Aldi, o => o.Price = 1.00m, observedOn: Today.AddDays(-9));

        var item = Of(shelf.Build(), line);

        item.Flag.Should().Be(PlanFlags.Stale);
        item.BestStore.Should().BeNull();
        item.Stores.Should().OnlyContain(s => s.State == PlanStoreStates.Stale);
    }

    [Fact]
    public void A_price_exactly_at_the_window_is_still_fresh()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        var line = shelf.Line(rice);
        shelf.Read(rice, Store.Lidl, o => o.Price = 2.00m, observedOn: Today.AddDays(-PromotionBuilder.StaleAfterDays));

        Of(shelf.Build(), line).BestStore.Should().Be("lidl");
    }

    [Fact]
    public void A_line_priced_only_by_out_of_stock_products_is_flagged_out_of_stock()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        var line = shelf.Line(rice);
        shelf.Read(rice, Store.Lidl, o => { o.Price = 2.00m; o.InStock = false; });

        var item = Of(shelf.Build(), line);

        item.Flag.Should().Be(PlanFlags.OutOfStock);
        item.BestStore.Should().BeNull();
    }

    // ---- single-store coverage ----------------------------------------------------------------------------------

    [Fact]
    public void One_store_pricing_every_line_is_named_with_the_stores_that_do()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var bread = shelf.Product("Pan 800g");
        shelf.Line(milk);
        shelf.Line(bread);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.00m);
        shelf.Read(milk, Store.Aldi, o => o.Price = 1.20m);
        shelf.Read(bread, Store.Lidl, o => o.Price = 1.60m);
        shelf.Read(bread, Store.Aldi, o => o.Price = 1.70m);
        shelf.Read(bread, Store.Tesco, o => o.Price = 1.10m); // cheapest bread, but Tesco does not sell the milk

        var coverage = shelf.Build().Coverage;

        coverage.SingleStore.Should().BeTrue();
        coverage.CoveredBy.Should().Equal("lidl", "aldi");
        coverage.Store.Should().Be("lidl", "it is best for the milk and aldi for nothing");
        coverage.ClosestStore.Should().BeNull();
        coverage.Blockers.Should().BeEmpty();
    }

    [Fact]
    public void Among_the_covering_stores_the_one_that_is_best_for_most_lines_is_named()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var bread = shelf.Product("Pan 800g");
        shelf.Line(milk);
        shelf.Line(bread);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m);
        shelf.Read(milk, Store.Aldi, o => o.Price = 1.00m);
        shelf.Read(bread, Store.Lidl, o => o.Price = 1.50m);
        shelf.Read(bread, Store.Aldi, o => o.Price = 1.40m);

        shelf.Build().Coverage.Store.Should().Be("aldi");
    }

    [Fact]
    public void When_no_store_prices_everything_the_closest_one_and_the_lines_it_cannot_price_are_named()
    {
        var shelf = new Shelf();
        var candles = shelf.FreeText("birthday candles");
        var milk = shelf.Product("Milk 1L");
        var bread = shelf.Product("Pan 800g");
        var eggs = shelf.Product("Eggs 12 pack");
        var butter = shelf.Product("Butter 250g");
        var salt = shelf.Product("Salt 1kg");
        foreach (var product in new[] { milk, bread, eggs, butter, salt })
        {
            shelf.Line(product);
        }

        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m);
        shelf.Read(milk, Store.Aldi, o => o.Price = 1.00m);
        shelf.Read(bread, Store.Lidl, o => o.Price = 1.50m);
        shelf.Link(bread, Store.Aldi); // linked but never read
        shelf.Read(eggs, Store.Aldi, o => o.Price = 3.00m);
        shelf.Read(butter, Store.Aldi, o => o.Price = 2.00m);
        shelf.Link(salt, Store.Tesco); // priced nowhere

        var plan = shelf.Build();

        plan.Coverage.SingleStore.Should().BeFalse();
        plan.Coverage.Store.Should().BeNull();
        plan.Coverage.CoveredBy.Should().BeEmpty();
        plan.Coverage.ClosestStore.Should().Be("aldi", "aldi prices milk, eggs and butter; lidl only milk and bread");
        plan.Coverage.Blockers.Select(b => (b.ItemId, b.Reason)).Should().Equal(
            (candles.Id, PlanFlags.Unmatched),
            (plan.Items.Single(i => i.ProductName == "Pan 800g").Id, PlanStoreStates.NotRead),
            (plan.Items.Single(i => i.ProductName == "Salt 1kg").Id, PlanFlags.NoPrice));
    }

    [Fact]
    public void A_line_no_store_can_price_blocks_the_whole_list_whatever_the_stores_say()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        shelf.Line(milk);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m);
        var candles = shelf.FreeText("birthday candles");
        var rice = shelf.Product("Rice 1kg");
        var riceLine = shelf.Line(rice);
        shelf.Read(rice, Store.Lidl, o => o.Price = 2.00m, observedOn: Today.AddDays(-10));

        var coverage = shelf.Build().Coverage;

        coverage.SingleStore.Should().BeFalse();
        coverage.ClosestStore.Should().Be("lidl");
        coverage.Blockers.Select(b => (b.ItemId, b.Reason)).Should().Equal(
            (candles.Id, PlanFlags.Unmatched),
            (riceLine.Id, PlanFlags.Stale));
    }

    [Fact]
    public void Lines_that_are_priced_but_not_comparable_still_count_as_covered()
    {
        var shelf = new Shelf();
        var skewers = shelf.Product("Chicken skewers");
        shelf.Line(skewers);
        shelf.Read(skewers, Store.Lidl, o => o.Price = 2.19m);
        shelf.Read(skewers, Store.Aldi, o => o.Price = 2.50m);

        var plan = shelf.Build();

        plan.Items.Single().Flag.Should().Be(PlanFlags.NotComparable);
        plan.Coverage.SingleStore.Should().BeTrue();
        plan.Coverage.CoveredBy.Should().Equal("lidl", "aldi");
    }

    [Fact]
    public void An_empty_list_has_no_covering_store()
    {
        var plan = new Shelf().Build();

        plan.Items.Should().BeEmpty();
        plan.Coverage.SingleStore.Should().BeFalse();
        plan.Coverage.Store.Should().BeNull();
        plan.Coverage.Blockers.Should().BeEmpty();
    }

    [Fact]
    public void When_nothing_is_priced_anywhere_every_line_blocks_with_its_own_reason()
    {
        var shelf = new Shelf();
        var line = shelf.FreeText("candles");

        var coverage = shelf.Build().Coverage;

        coverage.ClosestStore.Should().BeNull();
        coverage.Blockers.Should().ContainSingle().Which.Should().Be(new PlanBlocker(line.Id, "candles", PlanFlags.Unmatched));
    }

    // ---- the two-shop split -------------------------------------------------------------------------------------

    [Fact]
    public void When_no_one_shop_covers_the_list_the_cheapest_pair_of_shops_that_does_is_proposed_with_dated_prices()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var eggs = shelf.Product("Eggs 12 pack");
        var butter = shelf.Product("Butter 250g");
        foreach (var product in new[] { milk, eggs, butter })
        {
            shelf.Line(product);
        }

        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m);
        shelf.Read(milk, Store.Tesco, o => { o.Price = 1.30m; o.LoyaltyPrice = 1.00m; o.LoyaltyScheme = "Clubcard"; }, Today.AddDays(-1));
        shelf.Read(eggs, Store.Aldi, o => o.Price = 3.00m, Today.AddDays(-2));
        shelf.Read(eggs, Store.Dunnes, o => o.Price = 3.40m);
        shelf.Read(butter, Store.Aldi, o => o.Price = 2.00m, Today.AddDays(-2));
        shelf.Read(butter, Store.Tesco, o => o.Price = 2.30m);

        var split = shelf.Build().Coverage.Split;

        split.Should().NotBeNull();
        split!.Groups.Select(g => g.Store).Should().Equal("aldi", "tesco");
        split.Groups.Single(g => g.Store == "aldi").Lines.Select(l => (l.Text, l.Price, l.ObservedOn))
            .Should().Equal(("Eggs 12 pack", 3.00m, Today.AddDays(-2)), ("Butter 250g", 2.00m, Today.AddDays(-2)));
        var tesco = split.Groups.Single(g => g.Store == "tesco");
        tesco.Lines.Should().ContainSingle().Which.Should().Match<ByShopLine>(l => l.Text == "Milk 1L" && l.Price == 1.00m && l.Card == "Clubcard" && l.ObservedOn == Today.AddDays(-1));
        tesco.Total.Should().Be(1.00m);
        split.Total.Should().Be(6.00m, "the card price counts: milk at Tesco 1.00, eggs and butter at Aldi 5.00");
        split.OldestSeenOn.Should().Be(Today.AddDays(-2));
        split.Cards.Should().Equal("Clubcard");
    }

    [Fact]
    public void The_pair_is_chosen_by_the_lowest_total_not_by_the_shops_with_most_lines()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var eggs = shelf.Product("Eggs 12 pack");
        shelf.Line(milk);
        shelf.Line(eggs);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.00m);
        shelf.Read(eggs, Store.Aldi, o => o.Price = 3.00m);
        shelf.Read(milk, Store.Tesco, o => o.Price = 1.50m);
        shelf.Read(eggs, Store.Tesco, o => o.Price = 3.10m);
        shelf.Read(eggs, Store.Dunnes, o => o.Price = 2.50m);

        var plan = shelf.Build();

        // Tesco alone covers the list at 4.60, so no split is proposed.
        plan.Coverage.SingleStore.Should().BeTrue();
        plan.Coverage.Split.Should().BeNull();

        var third = shelf.Product("Butter 250g");
        shelf.Line(third);
        shelf.Read(third, Store.Aldi, o => o.Price = 2.00m);
        shelf.Read(third, Store.Dunnes, o => o.Price = 2.00m);

        var split = shelf.Build().Coverage.Split;

        split.Should().NotBeNull();
        split!.Groups.Select(g => (g.Store, g.Total)).Should().Equal(("lidl", 1.00m), ("dunnes", 4.50m));
        split.Total.Should().Be(5.50m);
    }

    [Fact]
    public void A_tie_goes_to_the_first_pair_in_the_fixed_shop_order()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var eggs = shelf.Product("Eggs 12 pack");
        shelf.Line(milk);
        shelf.Line(eggs);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.00m);
        shelf.Read(milk, Store.Tesco, o => o.Price = 1.00m);
        shelf.Read(eggs, Store.Aldi, o => o.Price = 2.00m);
        shelf.Read(eggs, Store.Dunnes, o => o.Price = 2.00m);

        shelf.Build().Coverage.Split!.Groups.Select(g => g.Store).Should().Equal("lidl", "aldi");
    }

    [Fact]
    public void A_split_counts_the_lines_whose_packs_differ_between_shops()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk");
        var eggs = shelf.Product("Eggs");
        shelf.Line(milk);
        shelf.Line(eggs);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.00m, title: "Milk 1L");
        shelf.Read(milk, Store.Aldi, o => o.Price = 1.40m, title: "Milk 2L");
        shelf.Read(eggs, Store.Tesco, o => o.Price = 3.00m, title: "Eggs 12 pack");

        var split = shelf.Build().Coverage.Split;

        split!.Groups.Select(g => g.Store).Should().Equal("lidl", "tesco");
        split.PacksDiffer.Should().Be(1, "milk is a litre at Lidl and two at Aldi; the eggs are priced at one shop only");
    }

    [Fact]
    public void No_split_is_proposed_when_a_line_has_no_price_anywhere_or_the_list_is_empty()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var eggs = shelf.Product("Eggs 12 pack");
        shelf.Line(milk);
        shelf.Line(eggs);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m);
        shelf.Read(eggs, Store.Aldi, o => o.Price = 3.00m);
        shelf.FreeText("birthday candles");

        shelf.Build().Coverage.Split.Should().BeNull("a split that leaves the candles out would not cover the list");
        new Shelf().Build().Coverage.Split.Should().BeNull();
    }

    [Fact]
    public void Old_and_out_of_stock_prices_never_count_towards_a_split()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk 1L");
        var eggs = shelf.Product("Eggs 12 pack");
        shelf.Line(milk);
        shelf.Line(eggs);
        shelf.Read(milk, Store.Lidl, o => o.Price = 1.20m);
        shelf.Read(eggs, Store.Aldi, o => o.Price = 3.00m, Today.AddDays(-10));
        shelf.Read(eggs, Store.Tesco, o => { o.Price = 2.00m; o.InStock = false; });

        shelf.Build().Coverage.Split.Should().BeNull();
    }

    // ---- the endpoint -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_endpoint_plans_only_the_open_lines_for_a_signed_in_person()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var today = PriceRecorder.DublinDay(_factory.Clock.GetUtcNow());
        var (open, ticked) = await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var milk = new Product { Id = Guid.NewGuid(), Name = "Milk 1L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Lidl, Url = "https://www.lidl.ie/p/milk/p11000", AddedAt = DateTimeOffset.UtcNow };
            var openLine = new ListItem { Id = Guid.NewGuid(), Text = milk.Name, ProductId = milk.Id, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            var tickedLine = new ListItem { Id = Guid.NewGuid(), Text = "old", IsChecked = true, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            var freeText = new ListItem { Id = Guid.NewGuid(), Text = "candles", AddedAt = DateTimeOffset.UtcNow.AddSeconds(1), AddedByUserId = user };
            db.Products.Add(milk);
            db.StoreListings.Add(listing);
            db.ListItems.AddRange(openLine, tickedLine, freeText);
            db.PriceObservations.AddRange(
                new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = today.AddDays(-1), ObservedAt = _factory.Clock.GetUtcNow(), Price = 1.99m, InStock = true },
                new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = today, ObservedAt = _factory.Clock.GetUtcNow(), Price = 1.29m, WasPrice = 1.49m, InStock = true });
            await db.SaveChangesAsync();
            return (openLine.Id, tickedLine.Id);
        });

        var plan = await owner.GetFromJsonAsync<JsonElement>("/api/plan");

        plan.GetProperty("staleAfterDays").GetInt32().Should().Be(PromotionBuilder.StaleAfterDays);
        var items = plan.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).Should().NotContain(ticked).And.Contain(open);
        var milkItem = items.Single(i => i.GetProperty("id").GetGuid() == open);
        milkItem.GetProperty("bestStore").GetString().Should().Be("lidl");
        milkItem.GetProperty("flag").ValueKind.Should().Be(JsonValueKind.Null);
        var lidl = milkItem.GetProperty("stores").EnumerateArray().Single();
        lidl.GetProperty("price").GetDecimal().Should().Be(1.29m, "the newest reading is the one used");
        lidl.GetProperty("wasPrice").GetDecimal().Should().Be(1.49m);
        items.Single(i => i.GetProperty("text").GetString() == "candles").GetProperty("flag").GetString().Should().Be("unmatched");
        var coverage = plan.GetProperty("coverage");
        coverage.GetProperty("singleStore").GetBoolean().Should().BeFalse();
        coverage.GetProperty("closestStore").GetString().Should().Be("lidl");
        coverage.GetProperty("blockers").EnumerateArray().Single().GetProperty("reason").GetString().Should().Be("unmatched");
    }

    [Fact]
    public async Task The_endpoint_refuses_the_anonymous()
    {
        (await _factory.NewBrowser().GetAsync("/api/plan")).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
