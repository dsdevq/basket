using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Lists;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

public sealed class ListTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public ListTests(BasketFactory factory) => _factory = factory;

    private static async Task<JsonElement> AddAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/list/items", new { text });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string[]> TextsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray()
        .Select(i => i.GetProperty("text").GetString()!).ToArray();

    [Fact]
    public async Task Both_people_see_the_same_list_and_who_added_what()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var (her, _) = await _factory.MemberAsync(owner, "Her");
        var marker = Guid.NewGuid().ToString("N")[..6];

        await AddAsync(her, $"spinach {marker}");
        _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await AddAsync(owner, $"butter {marker}");

        var mine = (await owner.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("text").GetString()!.EndsWith(marker, StringComparison.Ordinal)).ToArray();
        var hers = (await her.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("text").GetString()!.EndsWith(marker, StringComparison.Ordinal)).ToArray();
        mine.Select(i => i.GetProperty("id").GetGuid()).Should().Equal(hers.Select(i => i.GetProperty("id").GetGuid()));
        mine.Select(i => (i.GetProperty("text").GetString(), i.GetProperty("addedBy").GetProperty("name").GetString()))
            .Should().Equal(($"spinach {marker}", "Her"), ($"butter {marker}", "Owner"));
    }

    [Fact]
    public async Task Items_are_trimmed_and_blank_text_is_refused()
    {
        var (owner, _) = await _factory.OwnerAsync();

        var item = await AddAsync(owner, "   free   range\t eggs  ");
        item.GetProperty("text").GetString().Should().Be("free range eggs");

        foreach (var text in new[] { "", "   ", "\u0007\u0008" })
        {
            var response = await owner.PostAsJsonAsync("/api/list/items", new { text });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await owner.PostAsJsonAsync("/api/list/items", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Overlong_text_is_cut_to_the_limit()
    {
        var (owner, _) = await _factory.OwnerAsync();

        var item = await AddAsync(owner, new string('x', 900));

        item.GetProperty("text").GetString()!.Length.Should().Be(ListItem.MaxTextLength);
    }

    [Fact]
    public async Task Ticking_records_who_and_when_and_unticking_clears_it()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var (her, _) = await _factory.MemberAsync(owner, "Her");
        var id = (await AddAsync(owner, "tick me " + Guid.NewGuid().ToString("N")[..6])).GetProperty("id").GetGuid();

        var ticked = await her.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = true });
        ticked.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ticked.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("checked").GetBoolean().Should().BeTrue();
        body.GetProperty("checkedBy").GetProperty("name").GetString().Should().Be("Her");
        body.GetProperty("checkedAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        var unticked = await (await owner.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = false })).Content.ReadFromJsonAsync<JsonElement>();
        unticked.GetProperty("checked").GetBoolean().Should().BeFalse();
        unticked.GetProperty("checkedBy").ValueKind.Should().Be(JsonValueKind.Null);
        unticked.GetProperty("checkedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Ticked_items_sort_after_the_ones_still_to_buy()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var a = (await AddAsync(owner, "a")).GetProperty("id").GetGuid();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await AddAsync(owner, "b");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await AddAsync(owner, "c");

        await owner.PatchAsJsonAsync($"/api/list/items/{a}", new { @checked = true });

        (await TextsAsync(owner)).Should().Equal("b", "c", "a");
    }

    [Fact]
    public async Task A_missing_flag_or_unknown_id_is_refused()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var id = (await AddAsync(owner, "x" + Guid.NewGuid().ToString("N")[..6])).GetProperty("id").GetGuid();

        (await owner.PatchAsJsonAsync($"/api/list/items/{id}", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PatchAsJsonAsync($"/api/list/items/{Guid.NewGuid()}", new { @checked = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.DeleteAsync($"/api/list/items/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Either_person_can_delete_an_item_and_it_disappears_for_both()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var (her, _) = await _factory.MemberAsync(owner, "Her");
        var text = "delete me " + Guid.NewGuid().ToString("N")[..6];
        var id = (await AddAsync(owner, text)).GetProperty("id").GetGuid();

        (await her.DeleteAsync($"/api/list/items/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TextsAsync(owner)).Should().NotContain(text);
        (await TextsAsync(her)).Should().NotContain(text);
    }

    [Fact]
    public async Task The_list_stops_growing_at_the_cap()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var ownerId = await factory.WithDb(async db => (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Users)).Id);
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

        var response = await owner.PostAsJsonAsync("/api/list/items", new { text = "one too many" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Names_in_the_list_are_data_not_markup()
    {
        var (owner, _) = await _factory.OwnerAsync();

        var item = await AddAsync(owner, "<img src=x onerror=alert(1)>");

        item.GetProperty("text").GetString().Should().Be("<img src=x onerror=alert(1)>");
    }
}

internal static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(this HttpClient client, string uri, T value) =>
        client.PatchAsync(uri, JsonContent.Create(value));
}
