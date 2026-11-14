using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

public sealed class HabitTests
{
    private static async Task<Guid> ProductAsync(BasketFactory factory, string name) =>
        await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product.Id;
        });

    /// <summary>A closed trip: each product picked from the Shelf and ticked, then Done shopping.</summary>
    private static async Task TripAsync(BasketFactory factory, HttpClient client, params Guid[] products)
    {
        foreach (var product in products)
        {
            var added = await client.PostAsJsonAsync("/api/list/items", new { productId = product, store = (string?)null });
            var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await client.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.PostAsync("/api/list/close", null)).StatusCode.Should().Be(HttpStatusCode.Created);
        factory.Clock.Advance(TimeSpan.FromDays(7));
    }

    private static async Task<JsonElement[]> HabitsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/trips/habits")).GetProperty("products").EnumerateArray().ToArray();

    [Fact]
    public async Task No_trips_gives_no_habits()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();

        (await HabitsAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task Habits_need_a_signed_in_person()
    {
        await using var factory = new BasketFactory();

        (await factory.CreateClient().GetAsync("/api/trips/habits")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Products_rank_by_trips_that_bought_them_and_the_twice_bought_are_usual()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        var bread = await ProductAsync(factory, "Sliced pan");
        var caviar = await ProductAsync(factory, "Caviar");
        await TripAsync(factory, owner, milk, bread);
        await TripAsync(factory, owner, milk);
        await TripAsync(factory, owner, milk, caviar);

        var habits = await HabitsAsync(owner);

        habits.Select(h => h.GetProperty("name").GetString()).Should().Equal("Whole milk 2L", "Caviar", "Sliced pan");
        habits.Select(h => h.GetProperty("boughtIn").GetInt32()).Should().Equal(3, 1, 1);
        habits.Select(h => h.GetProperty("usual").GetBoolean()).Should().Equal(true, false, false);
    }

    [Fact]
    public async Task A_tie_goes_to_the_product_bought_most_recently()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var older = await ProductAsync(factory, "Older");
        var newer = await ProductAsync(factory, "Newer");
        await TripAsync(factory, owner, older);
        await TripAsync(factory, owner, newer);

        (await HabitsAsync(owner)).Select(h => h.GetProperty("name").GetString()).Should().Equal("Newer", "Older");
    }

    [Fact]
    public async Task Usual_looks_only_at_the_latest_ten_trips()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var old = await ProductAsync(factory, "Old habit");
        var filler = await ProductAsync(factory, "Filler");
        await TripAsync(factory, owner, old);
        await TripAsync(factory, owner, old);
        for (var i = 0; i < 10; i++)
        {
            await TripAsync(factory, owner, filler);
        }

        var habits = await HabitsAsync(owner);

        var oldHabit = habits.Single(h => h.GetProperty("name").GetString() == "Old habit");
        oldHabit.GetProperty("boughtIn").GetInt32().Should().Be(2);
        oldHabit.GetProperty("usual").GetBoolean().Should().BeFalse();
        habits.Single(h => h.GetProperty("name").GetString() == "Filler").GetProperty("usual").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Typed_lines_and_deleted_products_are_not_ranked()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var gone = await ProductAsync(factory, "Gone");
        await TripAsync(factory, owner, gone);
        var typed = await owner.PostAsJsonAsync("/api/list/items", new { text = "Birthday candles" });
        var id = (await typed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = true });
        await owner.PostAsync("/api/list/close", null);
        (await owner.DeleteAsync($"/api/products/{gone}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await HabitsAsync(owner)).Should().BeEmpty();
    }
}
