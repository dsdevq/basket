using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Lists;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

public sealed class TripTests
{
    private static async Task<Guid> AddAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/list/items", new { text });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task TickAsync(HttpClient client, Guid id, bool ticked = true) =>
        (await client.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = ticked })).StatusCode.Should().Be(HttpStatusCode.OK);

    private static async Task<string[]> TextsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray()
        .Select(i => i.GetProperty("text").GetString()!).ToArray();

    /// <summary>Whole milk linked at Tesco and Aldi, the way the Shelf offers it.</summary>
    private static async Task<Guid> SeedMilkAsync(BasketFactory factory) =>
        await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var milk = new Product { Id = Guid.NewGuid(), Name = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(milk);
            db.StoreListings.AddRange(
                new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Tesco, Url = "https://www.tesco.ie/groceries/en-IE/products/254317390", AddedAt = DateTimeOffset.UtcNow },
                new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Aldi, Url = "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541", AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return milk.Id;
        });

    /// <summary>Taps a Shelf tile: the product at a shop (or none), as the Shelf page posts it.</summary>
    private static async Task<HttpResponseMessage> PickAsync(HttpClient client, Guid productId, string? store) =>
        await client.PostAsJsonAsync("/api/list/items", new { productId, store });

    private static async Task<Guid> CloseAsync(HttpClient client) =>
        (await (await client.PostAsync("/api/list/close", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private static async Task<JsonElement[]> ItemsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray().ToArray();

    private static (string? Product, string? Store) Identity(JsonElement item) =>
        (item.GetProperty("productId").GetString(), item.GetProperty("store").GetString());

    private static async Task<JsonElement> TripsAsync(HttpClient client, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>("/api/trips" + query);

    [Fact]
    public async Task Done_shopping_files_the_ticked_items_with_date_and_who_ticked_and_empties_the_list()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        var milk = await AddAsync(owner, "Milk");
        var bread = await AddAsync(her, "Bread");
        await TickAsync(her, milk);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await TickAsync(owner, bread);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));

        var response = await owner.PostAsync("/api/list/close", null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var trip = await response.Content.ReadFromJsonAsync<JsonElement>();
        trip.GetProperty("closedAt").GetDateTimeOffset().Should().Be(factory.Clock.GetUtcNow());
        trip.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("text").GetString(), i.GetProperty("checkedBy").GetProperty("name").GetString()))
            .Should().Equal(("Milk", "Her"), ("Bread", "Owner"));
        (await TextsAsync(owner)).Should().BeEmpty();
        (await TextsAsync(her)).Should().BeEmpty();

        var history = await TripsAsync(her);
        history.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        var filed = history.GetProperty("trips").EnumerateArray().Single();
        filed.GetProperty("id").GetGuid().Should().Be(trip.GetProperty("id").GetGuid());
        filed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()).Should().Equal("Milk", "Bread");
    }

    [Fact]
    public async Task What_is_not_ticked_stays_on_the_list_for_the_next_shop()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await AddAsync(owner, "Milk");
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await AddAsync(owner, "Rice");
        await TickAsync(owner, milk);

        (await owner.PostAsync("/api/list/close", null)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await TextsAsync(owner)).Should().Equal("Rice");
        var trip = (await TripsAsync(owner)).GetProperty("trips").EnumerateArray().Single();
        trip.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()).Should().Equal("Milk");
    }

    [Fact]
    public async Task Nothing_ticked_files_no_trip()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await AddAsync(owner, "Milk");

        var response = await owner.PostAsync("/api/list/close", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("nothing_ticked");
        (await TripsAsync(owner)).GetProperty("trips").GetArrayLength().Should().Be(0);
        (await TextsAsync(owner)).Should().Equal("Milk");
    }

    [Fact]
    public async Task Two_devices_closing_at_once_file_one_trip()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        foreach (var text in new[] { "a", "b", "c", "d", "e" })
        {
            await TickAsync(owner, await AddAsync(owner, text));
        }

        var results = await Task.WhenAll(owner.PostAsync("/api/list/close", null), her.PostAsync("/api/list/close", null));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        var trips = (await TripsAsync(owner)).GetProperty("trips").EnumerateArray().ToArray();
        trips.Should().ContainSingle();
        trips[0].GetProperty("items").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task A_closed_trip_keeps_its_items_and_the_history_is_newest_first_in_pages()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var total = TripEndpoints.PageSize + 3;
        var closed = new List<Guid>();
        for (var i = 0; i < total; i++)
        {
            factory.Clock.Advance(TimeSpan.FromDays(1));
            await TickAsync(owner, await AddAsync(owner, $"trip {i}"));
            var created = await (await owner.PostAsync("/api/list/close", null)).Content.ReadFromJsonAsync<JsonElement>();
            closed.Add(created.GetProperty("id").GetGuid());
        }

        var first = await TripsAsync(owner);
        first.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var firstPage = first.GetProperty("trips").EnumerateArray().ToArray();
        firstPage.Select(t => t.GetProperty("id").GetGuid()).Should().Equal(Enumerable.Reverse(closed).Take(TripEndpoints.PageSize));

        var cursor = Uri.EscapeDataString(firstPage[^1].GetProperty("closedAt").GetDateTimeOffset().ToString("O"));
        var second = await TripsAsync(owner, $"?before={cursor}");
        second.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        second.GetProperty("trips").EnumerateArray().Select(t => t.GetProperty("id").GetGuid())
            .Should().Equal(Enumerable.Reverse(closed).Skip(TripEndpoints.PageSize));
    }

    [Fact]
    public async Task Same_as_last_time_puts_the_trip_back_on_the_list_as_to_buy_added_by_the_caller()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        foreach (var text in new[] { "Milk", "Bread", "Eggs" })
        {
            await TickAsync(owner, await AddAsync(owner, text));
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var tripId = (await (await owner.PostAsync("/api/list/close", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await TextsAsync(owner)).Should().BeEmpty();

        var reload = await her.PostAsync($"/api/trips/{tripId}/reload", null);

        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await reload.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("added").GetInt32().Should().Be(3);
        body.GetProperty("alreadyOnList").GetInt32().Should().Be(0);
        var items = (await owner.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray().ToArray();
        items.Select(i => i.GetProperty("text").GetString()).Should().Equal("Milk", "Bread", "Eggs");
        items.Should().OnlyContain(i => !i.GetProperty("checked").GetBoolean() && i.GetProperty("addedBy").GetProperty("name").GetString() == "Her");
        (await TripsAsync(owner)).GetProperty("trips").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Reloading_skips_what_is_already_waiting_on_the_list_so_a_second_tap_adds_nothing()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await TickAsync(owner, await AddAsync(owner, "Milk"));
        await TickAsync(owner, await AddAsync(owner, "Bread"));
        var tripId = (await (await owner.PostAsync("/api/list/close", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await AddAsync(owner, "MILK");

        var first = await (await owner.PostAsync($"/api/trips/{tripId}/reload", null)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await owner.PostAsync($"/api/trips/{tripId}/reload", null)).Content.ReadFromJsonAsync<JsonElement>();

        first.GetProperty("added").GetInt32().Should().Be(1);
        first.GetProperty("alreadyOnList").GetInt32().Should().Be(1);
        second.GetProperty("added").GetInt32().Should().Be(0);
        second.GetProperty("alreadyOnList").GetInt32().Should().Be(2);
        (await TextsAsync(owner)).Should().BeEquivalentTo("MILK", "Bread");
    }

    [Fact]
    public async Task A_ticked_line_with_the_same_text_does_not_stop_the_reload()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await TickAsync(owner, await AddAsync(owner, "Milk"));
        var tripId = (await (await owner.PostAsync("/api/list/close", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await TickAsync(owner, await AddAsync(owner, "Milk"));

        var body = await (await owner.PostAsync($"/api/trips/{tripId}/reload", null)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("added").GetInt32().Should().Be(1);
        (await TextsAsync(owner)).Should().Equal("Milk", "Milk");
    }

    [Fact]
    public async Task Reloading_an_unknown_trip_is_not_found_and_a_full_list_refuses()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        (await owner.PostAsync($"/api/trips/{Guid.NewGuid()}/reload", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await TickAsync(owner, await AddAsync(owner, "Milk"));
        var tripId = (await (await owner.PostAsync("/api/list/close", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var ownerId = await factory.WithDb(async db => (await db.Users.SingleAsync()).Id);
        await factory.WithDb(async db =>
        {
            db.ListItems.AddRange(Enumerable.Range(0, ListEndpoints.MaxItems).Select(i => new ListItem
            {
                Id = Guid.NewGuid(),
                Text = $"item {i}",
                AddedAt = DateTimeOffset.UtcNow,
                AddedByUserId = ownerId,
            }));
            await db.SaveChangesAsync();
            return 0;
        });

        var full = await owner.PostAsync($"/api/trips/{tripId}/reload", null);

        full.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await full.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("list_full");
    }

    [Fact]
    public async Task A_reloaded_shelf_line_keeps_its_product_and_shop_so_the_shelf_tile_adds_nothing_more()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAsync(factory);
        var picked = await (await PickAsync(owner, milk, "tesco")).Content.ReadFromJsonAsync<JsonElement>();
        await TickAsync(owner, picked.GetProperty("id").GetGuid());
        await TickAsync(owner, await AddAsync(owner, "Bread"));
        var tripId = await CloseAsync(owner);
        (await ItemsAsync(owner)).Should().BeEmpty();

        var reload = await owner.PostAsync($"/api/trips/{tripId}/reload", null);

        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await ItemsAsync(owner);
        items.Select(i => i.GetProperty("text").GetString()).Should().BeEquivalentTo("Whole milk 2L", "Bread");
        items.Select(Identity).Should().BeEquivalentTo(new[] { (milk.ToString(), "tesco"), ((string?)null, (string?)null) });

        // The Shelf tile now finds the open line instead of adding a second one.
        var tap = await PickAsync(owner, milk, "tesco");
        tap.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tap.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()
            .Should().Be(items.Single(i => i.GetProperty("productId").ValueKind == JsonValueKind.String).GetProperty("id").GetGuid());
        (await ItemsAsync(owner)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_shelf_line_picked_from_all_shops_reloads_with_no_shop_and_the_trip_history_shows_only_text()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAsync(factory);
        await TickAsync(owner, (await (await PickAsync(owner, milk, null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var tripId = await CloseAsync(owner);

        (await TripsAsync(owner)).GetProperty("trips")[0].GetProperty("items")[0].EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("id", "text", "checkedAt", "checkedBy");
        await owner.PostAsync($"/api/trips/{tripId}/reload", null);

        (await ItemsAsync(owner)).Select(Identity).Should().Equal((milk.ToString(), (string?)null));
        (await PickAsync(owner, milk, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ItemsAsync(owner)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_trip_closed_before_lines_carried_a_product_reloads_as_free_text()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var tripId = Guid.NewGuid();
        await factory.WithDb(async db =>
        {
            db.Trips.Add(new Trip { Id = tripId, ClosedAt = DateTimeOffset.UtcNow });
            db.TripItems.Add(new TripItem { Id = Guid.NewGuid(), TripId = tripId, Text = "Whole milk 2L", CheckedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        (await owner.PostAsync($"/api/trips/{tripId}/reload", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ItemsAsync(owner)).Select(Identity).Should().Equal(((string?)null, (string?)null));
    }

    [Fact]
    public async Task Reloading_skips_a_product_already_waiting_at_that_shop_even_when_its_text_differs_but_adds_it_for_another_shop()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAsync(factory);
        foreach (var shop in new[] { "tesco", "aldi" })
        {
            await TickAsync(owner, (await (await PickAsync(owner, milk, shop)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        var tripId = await CloseAsync(owner);
        await PickAsync(owner, milk, "tesco");
        await factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteUpdateAsync(s => s.SetProperty(i => i.Text, "Milk, renamed"));
            return 0;
        });

        var body = await (await owner.PostAsync($"/api/trips/{tripId}/reload", null)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("added").GetInt32().Should().Be(1);
        body.GetProperty("alreadyOnList").GetInt32().Should().Be(1);
        (await ItemsAsync(owner)).Select(Identity).Should().BeEquivalentTo(new[] { (milk.ToString(), "tesco"), (milk.ToString(), "aldi") });
    }

    [Fact]
    public async Task Reloading_twice_at_once_leaves_one_line_for_the_product()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        var milk = await SeedMilkAsync(factory);
        await TickAsync(owner, (await (await PickAsync(owner, milk, "tesco")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var tripId = await CloseAsync(owner);

        var results = await Task.WhenAll(owner.PostAsync($"/api/trips/{tripId}/reload", null), her.PostAsync($"/api/trips/{tripId}/reload", null));

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await ItemsAsync(owner)).Select(Identity).Should().Equal((milk.ToString(), "tesco"));
    }

    [Fact]
    public async Task A_product_deleted_after_the_trip_closed_reloads_as_free_text()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAsync(factory);
        await TickAsync(owner, (await (await PickAsync(owner, milk, "tesco")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var tripId = await CloseAsync(owner);
        await factory.WithDb(async db =>
        {
            await db.Products.Where(p => p.Id == milk).ExecuteDeleteAsync();
            return 0;
        });

        (await owner.PostAsync($"/api/trips/{tripId}/reload", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var item = (await ItemsAsync(owner)).Single();
        item.GetProperty("text").GetString().Should().Be("Whole milk 2L");
        Identity(item).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task A_trip_outlives_the_account_that_ticked_it_and_shows_no_name()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        await TickAsync(her, await AddAsync(owner, "Milk"));
        await owner.PostAsync("/api/list/close", null);
        var herId = await factory.WithDb(async db => (await db.Users.SingleAsync(u => u.DisplayName == "Her")).Id);

        await factory.WithDb(async db =>
        {
            await db.Users.Where(u => u.Id == herId).ExecuteDeleteAsync();
            return 0;
        });

        var item = (await TripsAsync(owner)).GetProperty("trips")[0].GetProperty("items")[0];
        item.GetProperty("text").GetString().Should().Be("Milk");
        item.GetProperty("checkedBy").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_closed_trip_tells_a_typed_item_from_a_shelf_pick_so_only_the_typed_one_offers_promote()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAsync(factory);
        await AddAsync(owner, "Oat drink");
        (await PickAsync(owner, milk, "tesco")).EnsureSuccessStatusCode();
        foreach (var item in await ItemsAsync(owner))
        {
            await TickAsync(owner, item.GetProperty("id").GetGuid());
        }

        await CloseAsync(owner);

        var items = (await TripsAsync(owner)).GetProperty("trips")[0].GetProperty("items").EnumerateArray().ToArray();
        items.Single(i => i.GetProperty("text").GetString() == "Oat drink").GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);
        items.Single(i => i.GetProperty("text").GetString() == "Whole milk 2L").GetProperty("productId").GetGuid().Should().Be(milk);
    }
}
