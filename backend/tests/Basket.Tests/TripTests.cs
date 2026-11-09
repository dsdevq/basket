using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Lists;
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
}
