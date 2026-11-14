using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Data.Migrations;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>Ticking a line records it in the open trip: the item, the shop it was bought at (the plan's, unless changed) and the time.</summary>
public sealed class OpenTripTests
{
    private static async Task<Guid> AddAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/list/items", new { text });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> TickAsync(HttpClient client, Guid id, bool ticked = true)
    {
        var response = await client.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = ticked });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A product the Shelf would offer, read at Lidl only today, so the plan names Lidl best for it.</summary>
    private static async Task<Guid> SeedMilkAtLidlAsync(BasketFactory factory)
    {
        var today = PriceRecorder.DublinDay(factory.Clock.GetUtcNow());
        return await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var milk = new Product { Id = Guid.NewGuid(), Name = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Lidl, Url = "https://www.lidl.ie/p/milk/p11000", AddedAt = DateTimeOffset.UtcNow };
            var aldi = new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Aldi, Url = "https://www.aldi.ie/product/milk-000000000000422541", AddedAt = DateTimeOffset.UtcNow };
            db.Products.Add(milk);
            db.StoreListings.AddRange(listing, aldi);
            db.PriceObservations.Add(new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = today, ObservedAt = factory.Clock.GetUtcNow(), Price = 1.29m, InStock = true });
            await db.SaveChangesAsync();
            return milk.Id;
        });
    }

    private static async Task<Guid> PickAsync(HttpClient client, Guid productId, string? store)
    {
        var response = await client.PostAsJsonAsync("/api/list/items", new { productId, store });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(Trip Trip, TripItem[] Items)[]> OpenTripsAsync(BasketFactory factory) =>
        await factory.WithDb(async db =>
        {
            var trips = await db.Trips.AsNoTracking().Where(t => t.ClosedAt == null).ToListAsync();
            var items = await db.TripItems.AsNoTracking().ToListAsync();
            return trips.Select(t => (t, items.Where(i => i.TripId == t.Id).OrderBy(i => i.CheckedAt).ToArray())).ToArray();
        });

    [Fact]
    public async Task Ticking_records_the_item_the_planned_shop_and_the_time_in_the_open_trip()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAtLidlAsync(factory);
        var line = await PickAsync(owner, milk, null);
        factory.Clock.Advance(TimeSpan.FromMinutes(3));

        var ticked = await TickAsync(owner, line);

        ticked.GetProperty("boughtStore").GetString().Should().Be("lidl");
        var open = (await OpenTripsAsync(factory)).Should().ContainSingle().Subject;
        open.Trip.ClosedAt.Should().BeNull();
        var recorded = open.Items.Should().ContainSingle().Subject;
        recorded.Text.Should().Be("Whole milk 2L");
        recorded.BoughtStore.Should().Be(Store.Lidl);
        recorded.ProductId.Should().Be(milk);
        recorded.Store.Should().BeNull("the line was picked from All");
        recorded.CheckedAt.Should().BeCloseTo(factory.Clock.GetUtcNow(), TimeSpan.FromMilliseconds(1));
        recorded.CheckedByUserId.Should().NotBeNull();
        recorded.ListItemId.Should().Be(line);
        (await owner.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items")[0].GetProperty("boughtStore").GetString().Should().Be("lidl");
    }

    [Fact]
    public async Task A_line_picked_at_a_shop_is_bought_there_and_free_text_has_no_shop()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAtLidlAsync(factory);
        var picked = await PickAsync(owner, milk, "aldi");
        var bread = await AddAsync(owner, "Bread");

        (await TickAsync(owner, picked)).GetProperty("boughtStore").GetString().Should().Be("aldi");
        (await TickAsync(owner, bread)).GetProperty("boughtStore").ValueKind.Should().Be(JsonValueKind.Null);

        var open = (await OpenTripsAsync(factory)).Single();
        open.Items.Select(i => (i.Text, i.BoughtStore)).Should().BeEquivalentTo(new (string, Store?)[] { ("Whole milk 2L", Store.Aldi), ("Bread", null) });
    }

    [Fact]
    public async Task The_person_can_change_the_shop_and_ticking_again_keeps_the_change()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        var milk = await SeedMilkAtLidlAsync(factory);
        var line = await PickAsync(owner, milk, null);
        await TickAsync(owner, line);

        var changed = await owner.PatchAsJsonAsync($"/api/list/items/{line}/bought-at", new { store = "dunnes" });

        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boughtStore").GetString().Should().Be("dunnes");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync(her, line);
        var recorded = (await OpenTripsAsync(factory)).Single().Items.Single();
        recorded.BoughtStore.Should().Be(Store.Dunnes, "a second phone ticking the same line does not undo the change");
        recorded.CheckedAt.Should().BeCloseTo(factory.Clock.GetUtcNow(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Changing_the_shop_needs_a_known_shop_and_a_ticked_item()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var line = await AddAsync(owner, "Milk");

        var open = await owner.PatchAsJsonAsync($"/api/list/items/{line}/bought-at", new { store = "aldi" });
        open.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("not_ticked");

        await TickAsync(owner, line);
        var unknown = await owner.PatchAsJsonAsync($"/api/list/items/{line}/bought-at", new { store = "costco" });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PatchAsJsonAsync($"/api/list/items/{line}/bought-at", new { store = (string?)null })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PatchAsJsonAsync($"/api/list/items/{Guid.NewGuid()}/bought-at", new { store = "aldi" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unticking_or_deleting_a_ticked_line_takes_its_record_out_of_the_open_trip()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await AddAsync(owner, "Milk");
        var bread = await AddAsync(owner, "Bread");
        var eggs = await AddAsync(owner, "Eggs");
        foreach (var line in new[] { milk, bread, eggs })
        {
            await TickAsync(owner, line);
        }

        var unticked = await TickAsync(owner, milk, ticked: false);
        (await owner.DeleteAsync($"/api/list/items/{bread}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        unticked.GetProperty("boughtStore").ValueKind.Should().Be(JsonValueKind.Null);
        (await OpenTripsAsync(factory)).Single().Items.Select(i => i.Text).Should().Equal("Eggs");
    }

    [Fact]
    public async Task Ticks_from_several_phones_at_once_share_one_open_trip()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, _) = await factory.MemberAsync(owner, "Her");
        var lines = new List<Guid>();
        foreach (var text in new[] { "a", "b", "c", "d", "e", "f" })
        {
            lines.Add(await AddAsync(owner, text));
        }

        await Task.WhenAll(lines.Select((line, i) => (i % 2 == 0 ? owner : her).PatchAsJsonAsync($"/api/list/items/{line}", new { @checked = true })));

        var open = (await OpenTripsAsync(factory)).Should().ContainSingle().Subject;
        open.Items.Should().HaveCount(6);
    }

    [Fact]
    public async Task Done_shopping_closes_the_open_trip_with_its_shops_and_the_next_tick_opens_a_new_one()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAtLidlAsync(factory);
        await TickAsync(owner, await PickAsync(owner, milk, null));
        await owner.PatchAsJsonAsync($"/api/list/items/{(await owner.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items")[0].GetProperty("id").GetGuid()}/bought-at", new { store = "tesco" });
        await TickAsync(owner, await AddAsync(owner, "Bread"));
        factory.Clock.Advance(TimeSpan.FromHours(1));

        var closed = await owner.PostAsync("/api/list/close", null);

        closed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await OpenTripsAsync(factory)).Should().BeEmpty("closing the trip leaves no open one until something is ticked");
        var tripId = (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var saved = await factory.WithDb(db => db.TripItems.AsNoTracking().Where(i => i.TripId == tripId).OrderBy(i => i.Text).ToListAsync());
        saved.Select(i => (i.Text, i.BoughtStore)).Should().Equal(("Bread", null), ("Whole milk 2L", Store.Tesco));
        saved.Should().OnlyContain(i => i.ListItemId == null, "the lines left the list");

        await TickAsync(owner, await AddAsync(owner, "Rice"));
        var next = (await OpenTripsAsync(factory)).Should().ContainSingle().Subject;
        next.Trip.Id.Should().NotBe(tripId);
        next.Items.Select(i => i.Text).Should().Equal("Rice");
        (await owner.GetFromJsonAsync<JsonElement>("/api/trips")).GetProperty("trips").EnumerateArray()
            .Select(t => t.GetProperty("id").GetGuid()).Should().Equal(tripId);
    }

    [Fact]
    public async Task The_open_trip_is_not_history_and_counts_for_no_habit()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAtLidlAsync(factory);
        await TickAsync(owner, await PickAsync(owner, milk, null));

        (await owner.GetFromJsonAsync<JsonElement>("/api/trips")).GetProperty("trips").GetArrayLength().Should().Be(0);
        (await owner.GetFromJsonAsync<JsonElement>("/api/trips/habits")).GetProperty("products").GetArrayLength().Should().Be(0);
        (await owner.GetFromJsonAsync<JsonElement>("/api/scorecard")).GetProperty("trips").GetInt32().Should().Be(0);
        var open = (await OpenTripsAsync(factory)).Single();
        (await owner.PostAsync($"/api/trips/{open.Trip.Id}/reload", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lines_ticked_before_trips_were_recorded_join_the_open_trip_by_the_migration_and_by_closing()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await SeedMilkAtLidlAsync(factory);
        var by = await factory.WithDb(db => db.Users.Select(u => u.Id).FirstAsync());
        await factory.WithDb(async db =>
        {
            db.ListItems.AddRange(
                new ListItem { Id = Guid.NewGuid(), Text = "Whole milk 2L", ProductId = milk, Store = Store.Aldi, IsChecked = true, CheckedAt = DateTimeOffset.UtcNow, CheckedByUserId = by, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by },
                new ListItem { Id = Guid.NewGuid(), Text = "Bread", IsChecked = true, CheckedAt = DateTimeOffset.UtcNow, CheckedByUserId = by, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by },
                new ListItem { Id = Guid.NewGuid(), Text = "Rice", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by });
            await db.SaveChangesAsync();
            return 0;
        });

        await factory.WithDb(async db => await db.Database.ExecuteSqlRawAsync(OpenTrip.BackfillSql));
        await factory.WithDb(async db => await db.Database.ExecuteSqlRawAsync(OpenTrip.BackfillSql));

        var open = (await OpenTripsAsync(factory)).Should().ContainSingle("running it again adds nothing").Subject;
        open.Items.Select(i => (i.Text, i.BoughtStore)).Should().BeEquivalentTo(new (string, Store?)[] { ("Whole milk 2L", Store.Aldi), ("Bread", null) });

        // A line ticked in the window between the migration and the new code (or any record that went missing) is added at close.
        await factory.WithDb(async db => await db.TripItems.ExecuteDeleteAsync());
        var closed = await owner.PostAsync("/api/list/close", null);
        closed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString())
            .Should().BeEquivalentTo("Whole milk 2L", "Bread");
    }
}
