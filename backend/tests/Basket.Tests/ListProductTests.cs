using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>List lines picked from the Shelf: linked to a product and the shop they were picked from.</summary>
public sealed class ListProductTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public ListProductTests(BasketFactory factory) => _factory = factory;

    /// <summary>An empty list and catalog, then Milk linked at Tesco and Aldi and Bread at Lidl only.</summary>
    private async Task<(HttpClient Owner, Guid Milk, Guid Bread)> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        return await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var milk = new Product { Id = Guid.NewGuid(), Name = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            var bread = new Product { Id = Guid.NewGuid(), Name = "Sliced pan", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.AddRange(milk, bread);
            db.StoreListings.AddRange(
                new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Tesco, Url = "https://www.tesco.ie/groceries/en-IE/products/254317390", AddedAt = DateTimeOffset.UtcNow },
                new StoreListing { Id = Guid.NewGuid(), ProductId = milk.Id, Store = Store.Aldi, Url = "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541", AddedAt = DateTimeOffset.UtcNow },
                new StoreListing { Id = Guid.NewGuid(), ProductId = bread.Id, Store = Store.Lidl, Url = "https://www.lidl.ie/p/sliced-pan/p11000001", AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return (owner, milk.Id, bread.Id);
        });
    }

    private static async Task<JsonElement[]> ItemsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task A_picked_product_is_named_after_the_product_and_remembers_its_shop()
    {
        var (owner, milk, _) = await SeedAsync();

        var response = await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "Tesco", text = "ignored" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await response.Content.ReadFromJsonAsync<JsonElement>();
        item.GetProperty("text").GetString().Should().Be("Whole milk 2L");
        item.GetProperty("productId").GetGuid().Should().Be(milk);
        item.GetProperty("store").GetString().Should().Be("tesco");
        (await ItemsAsync(owner)).Single().GetProperty("store").GetString().Should().Be("tesco");
    }

    [Fact]
    public async Task A_free_text_line_has_no_product_and_no_shop()
    {
        var (owner, _, _) = await SeedAsync();

        var response = await owner.PostAsJsonAsync("/api/list/items", new { text = "birthday candles" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await response.Content.ReadFromJsonAsync<JsonElement>();
        item.GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("store").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Picking_the_same_product_and_shop_again_returns_the_open_line_instead_of_a_second_one()
    {
        var (owner, milk, _) = await SeedAsync();
        var first = await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "tesco" });
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var again = await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "tesco" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid().Should().Be(firstId);
        (await ItemsAsync(owner)).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_same_product_from_another_shop_or_from_all_shops_is_its_own_line()
    {
        var (owner, milk, _) = await SeedAsync();

        (await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "tesco" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "aldi" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await owner.PostAsJsonAsync("/api/list/items", new { productId = milk })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await owner.PostAsJsonAsync("/api/list/items", new { productId = milk })).StatusCode.Should().Be(HttpStatusCode.OK, "a null shop is one value too");

        (await ItemsAsync(owner)).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_ticked_line_does_not_block_buying_the_product_again_and_unticking_into_a_duplicate_is_refused()
    {
        var (owner, milk, _) = await SeedAsync();
        var first = await (await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "tesco" })).Content.ReadFromJsonAsync<JsonElement>();
        var firstId = first.GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/list/items/{firstId}", new { @checked = true })).EnsureSuccessStatusCode();

        var second = await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "tesco" });
        second.StatusCode.Should().Be(HttpStatusCode.Created);

        var untick = await owner.PatchAsJsonAsync($"/api/list/items/{firstId}", new { @checked = false });
        untick.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await untick.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("already_on_list");
        (await ItemsAsync(owner)).Single(i => i.GetProperty("id").GetGuid() == firstId).GetProperty("checked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Two_picks_at_the_same_moment_end_as_one_line()
    {
        var (owner, milk, _) = await SeedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "aldi" })));

        results.Select(r => r.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.Created || c == HttpStatusCode.OK);
        (await ItemsAsync(owner)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_pick_that_names_something_the_shelf_does_not_have_is_refused()
    {
        var (owner, milk, bread) = await SeedAsync();

        async Task<(HttpStatusCode Status, string? Error)> TryAsync(object body)
        {
            var response = await owner.PostAsJsonAsync("/api/list/items", body);
            var error = response.IsSuccessStatusCode ? null : (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
            return (response.StatusCode, error);
        }

        (await TryAsync(new { productId = Guid.NewGuid() })).Should().Be((HttpStatusCode.BadRequest, "product_unknown"));
        (await TryAsync(new { productId = milk, store = "waitrose" })).Should().Be((HttpStatusCode.BadRequest, "store_unknown"));
        (await TryAsync(new { productId = bread, store = "tesco" })).Should().Be((HttpStatusCode.BadRequest, "not_sold_there"));
        (await TryAsync(new { text = "candles", store = "tesco" })).Should().Be((HttpStatusCode.BadRequest, "store_needs_product"));
        (await ItemsAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_product_keeps_its_list_line_as_plain_text()
    {
        var (owner, milk, _) = await SeedAsync();
        (await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = "tesco" })).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/products/{milk}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var item = (await ItemsAsync(owner)).Single();
        item.GetProperty("text").GetString().Should().Be("Whole milk 2L");
        item.GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
