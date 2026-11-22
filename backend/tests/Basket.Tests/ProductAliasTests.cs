using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// A product's other names, and what they do: link a typed list line, show on the product, and (in English) widen what the matcher
/// finds. Synthetic products only.
/// </summary>
public sealed class ProductAliasTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public ProductAliasTests(BasketFactory factory) => _factory = factory;

    private async Task<(HttpClient Owner, Guid Eggs, Guid Milk)> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        return await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Trips.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            await db.StoreProducts.ExecuteDeleteAsync();
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var eggs = new Product { Id = Guid.NewGuid(), Name = "Free Range Eggs", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            var milk = new Product { Id = Guid.NewGuid(), Name = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.AddRange(eggs, milk);
            await db.SaveChangesAsync();
            return (owner, eggs.Id, milk.Id);
        });
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Guid> AddAliasAsync(HttpClient owner, Guid product, string text)
    {
        var response = await owner.PostAsJsonAsync($"/api/products/{product}/aliases", new { text });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddLineAsync(HttpClient owner, string text)
    {
        var response = await owner.PostAsJsonAsync("/api/list/items", new { text });
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<string[]> AliasesOfAsync(HttpClient owner, Guid product) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/products")).GetProperty("products").EnumerateArray()
        .Single(p => p.GetProperty("id").GetGuid() == product)
        .GetProperty("aliases").EnumerateArray().Select(a => a.GetProperty("text").GetString()!).ToArray();

    // ---- the names themselves ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_product_lists_its_other_names_in_the_order_they_were_added()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddAliasAsync(owner, eggs, "Яйця");
        _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await AddAliasAsync(owner, eggs, "  Яйця   курячі ");

        (await AliasesOfAsync(owner, eggs)).Should().Equal("Яйця", "Яйця курячі");
    }

    [Fact]
    public async Task A_name_is_refused_when_it_is_empty_the_products_own_or_already_taken_by_any_product()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await AddAliasAsync(owner, eggs, "Яйця");

        var empty = await owner.PostAsJsonAsync($"/api/products/{eggs}/aliases", new { text = "   " });
        var own = await owner.PostAsJsonAsync($"/api/products/{eggs}/aliases", new { text = "free range EGGS" });
        var same = await owner.PostAsJsonAsync($"/api/products/{eggs}/aliases", new { text = "яйця" });
        var other = await owner.PostAsJsonAsync($"/api/products/{milk}/aliases", new { text = "ЯЙЦЯ" });
        var unknown = await owner.PostAsJsonAsync($"/api/products/{Guid.NewGuid()}/aliases", new { text = "x" });

        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(own)).GetProperty("error").GetString().Should().Be("alias_is_name");
        (await ReadAsync(same)).GetProperty("error").GetString().Should().Be("alias_exists");
        var taken = await ReadAsync(other);
        other.StatusCode.Should().Be(HttpStatusCode.Conflict);
        taken.GetProperty("message").GetString().Should().Contain("Free Range Eggs");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AliasesOfAsync(owner, milk)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_product_keeps_at_most_twenty_other_names()
    {
        var (owner, eggs, _) = await SeedAsync();
        for (var i = 0; i < ProductAlias.MaxPerProduct; i++)
        {
            await AddAliasAsync(owner, eggs, $"name {i}");
        }

        var over = await owner.PostAsJsonAsync($"/api/products/{eggs}/aliases", new { text = "one more" });

        over.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync(over)).GetProperty("error").GetString().Should().Be("aliases_full");
    }

    [Fact]
    public async Task A_name_can_be_renamed_even_in_another_letter_case_and_deleted()
    {
        var (owner, eggs, milk) = await SeedAsync();
        var id = await AddAliasAsync(owner, eggs, "яйця");
        await AddAliasAsync(owner, milk, "Молоко");

        var recased = await owner.PatchAsJsonAsync($"/api/products/{eggs}/aliases/{id}", new { text = "Яйця" });
        var clash = await owner.PatchAsJsonAsync($"/api/products/{eggs}/aliases/{id}", new { text = "молоко" });
        var wrongProduct = await owner.PatchAsJsonAsync($"/api/products/{milk}/aliases/{id}", new { text = "Яйця2" });
        var renamed = await owner.PatchAsJsonAsync($"/api/products/{eggs}/aliases/{id}", new { text = "Яйця курячі" });

        recased.StatusCode.Should().Be(HttpStatusCode.OK);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        wrongProduct.StatusCode.Should().Be(HttpStatusCode.NotFound);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AliasesOfAsync(owner, eggs)).Should().Equal("Яйця курячі");

        (await owner.DeleteAsync($"/api/products/{eggs}/aliases/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.DeleteAsync($"/api/products/{eggs}/aliases/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AliasesOfAsync(owner, eggs)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_product_takes_its_names_with_it()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddAliasAsync(owner, eggs, "Яйця");

        await owner.DeleteAsync($"/api/products/{eggs}");

        (await _factory.WithDb(db => db.ProductAliases.CountAsync())).Should().Be(0);
    }

    // ---- typing a name -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_typed_line_whose_words_are_another_name_goes_on_the_list_linked_to_the_product_keeping_its_words()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddAliasAsync(owner, eggs, "Яйця");

        var response = await owner.PostAsJsonAsync("/api/list/items", new { text = "  яйця " });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var line = await ReadAsync(response);
        line.GetProperty("text").GetString().Should().Be("яйця");
        line.GetProperty("productId").GetGuid().Should().Be(eggs);
        line.GetProperty("store").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Typing_a_name_of_a_product_already_waiting_returns_that_line()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddAliasAsync(owner, eggs, "Яйця");
        var first = await AddLineAsync(owner, "Яйця");

        var again = await owner.PostAsJsonAsync("/api/list/items", new { text = "яйця" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(again)).GetProperty("id").GetGuid().Should().Be(first);
        (await _factory.WithDb(db => db.ListItems.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Words_that_are_no_ones_name_stay_a_typed_line()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddAliasAsync(owner, eggs, "Яйця");

        var response = await owner.PostAsJsonAsync("/api/list/items", new { text = "Креветки 120 грам" });

        (await ReadAsync(response)).GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- linking a typed line ----------------------------------------------------------------------------------

    [Fact]
    public async Task Linking_a_typed_line_keeps_its_words_and_remembering_saves_them_as_a_name()
    {
        var (owner, eggs, _) = await SeedAsync();
        var line = await AddLineAsync(owner, "Яйця");

        var response = await owner.PutAsJsonAsync($"/api/list/items/{line}/product", new { productId = eggs, remember = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var linked = await ReadAsync(response);
        linked.GetProperty("text").GetString().Should().Be("Яйця");
        linked.GetProperty("productId").GetGuid().Should().Be(eggs);
        (await AliasesOfAsync(owner, eggs)).Should().Equal("Яйця");
        // The next time the same words are typed they link on their own.
        var next = await ReadAsync(await owner.PostAsJsonAsync("/api/list/items", new { text = "Яйця" }));
        next.GetProperty("id").GetGuid().Should().Be(line);
    }

    [Fact]
    public async Task Linking_without_remembering_links_this_line_once()
    {
        var (owner, eggs, _) = await SeedAsync();
        var line = await AddLineAsync(owner, "Яйця");

        var response = await owner.PutAsJsonAsync($"/api/list/items/{line}/product", new { productId = eggs, remember = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(response)).GetProperty("productId").GetGuid().Should().Be(eggs);
        (await AliasesOfAsync(owner, eggs)).Should().BeEmpty();
        var second = await ReadAsync(await owner.PostAsJsonAsync("/api/list/items", new { text = "Яйця" }));
        second.GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Linking_a_line_already_ticked_into_the_open_trip_gives_the_trip_item_the_product_too()
    {
        var (owner, eggs, _) = await SeedAsync();
        var line = await AddLineAsync(owner, "Яйця");
        (await owner.PatchAsJsonAsync($"/api/list/items/{line}", new { @checked = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await owner.PutAsJsonAsync($"/api/list/items/{line}/product", new { productId = eggs, remember = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var trip = await _factory.WithDb(db => db.TripItems.AsNoTracking().SingleAsync(t => t.ListItemId == line));
        trip.ProductId.Should().Be(eggs);
        trip.Text.Should().Be("Яйця");
    }

    [Fact]
    public async Task Linking_with_remember_does_not_save_words_that_are_the_products_own_name()
    {
        var (owner, eggs, _) = await SeedAsync();
        var line = await AddLineAsync(owner, "free range eggs");

        var response = await owner.PutAsJsonAsync($"/api/list/items/{line}/product", new { productId = eggs, remember = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AliasesOfAsync(owner, eggs)).Should().BeEmpty();
    }

    [Fact]
    public async Task Linking_is_refused_for_an_unknown_product_a_linked_line_a_duplicate_line_and_words_that_mean_another_product()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await AddAliasAsync(owner, milk, "Молоко");
        var typed = await AddLineAsync(owner, "Молоко");
        // "Молоко" was typed before it was a name: it is a typed line that the person now links to the wrong product.
        await _factory.WithDb(async db =>
        {
            await db.ListItems.Where(i => i.Id == typed).ExecuteUpdateAsync(s => s.SetProperty(i => i.ProductId, (Guid?)null));
            return 0;
        });
        var pickedEggs = (await ReadAsync(await owner.PostAsJsonAsync("/api/list/items", new { productId = eggs }))).GetProperty("id").GetGuid();
        var another = await AddLineAsync(owner, "ще яйця");
        var butter = await _factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Butter 250g", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product.Id;
        });

        var unknown = await owner.PutAsJsonAsync($"/api/list/items/{typed}/product", new { productId = Guid.NewGuid() });
        var missing = await owner.PutAsJsonAsync($"/api/list/items/{Guid.NewGuid()}/product", new { productId = eggs });
        var already = await owner.PutAsJsonAsync($"/api/list/items/{pickedEggs}/product", new { productId = milk });
        var duplicate = await owner.PutAsJsonAsync($"/api/list/items/{another}/product", new { productId = eggs });
        var meansOther = await owner.PutAsJsonAsync($"/api/list/items/{typed}/product", new { productId = butter, remember = true });

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(already)).GetProperty("error").GetString().Should().Be("already_linked");
        (await ReadAsync(duplicate)).GetProperty("error").GetString().Should().Be("already_on_list");
        var refused = await ReadAsync(meansOther);
        meansOther.StatusCode.Should().Be(HttpStatusCode.Conflict);
        refused.GetProperty("error").GetString().Should().Be("alias_exists");
        // Nothing was half done: the refused line is still typed text.
        (await _factory.WithDb(db => db.ListItems.AsNoTracking().SingleAsync(i => i.Id == typed))).ProductId.Should().BeNull();
    }

    // ---- the matcher -------------------------------------------------------------------------------------------

    [Fact]
    public void An_english_name_is_one_more_name_for_the_matcher_and_a_cyrillic_one_is_ignored()
    {
        var product = new ProductFacts("Dairy drink 2L", null);
        var row = new CandidateFacts("Fresh Whole Milk", null, null, ProductSize.Parse("2 L"));
        CandidateMatcher.Evaluate(product, row).Should().BeNull();

        var names = ProductAliases.FactsWithAliases(product, ["Молоко", "Whole milk 2L"]);

        names.Should().HaveCount(2);
        names[1].Name.Should().Be("Whole milk 2L");
        CandidateMatcher.EvaluateAny(names, row).Should().NotBeNull();
        CandidateMatcher.EvaluateAny(ProductAliases.FactsWithAliases(product, ["Молоко"]), row).Should().BeNull();
    }

    [Fact]
    public async Task An_english_name_makes_the_server_propose_a_page_the_product_name_alone_missed()
    {
        var (owner, _, _) = await SeedAsync();
        var item = new CatalogItem("254300011", "https://www.tesco.ie/groceries/en-IE/products/254300011", "Fresh Whole Milk", null, "2 L", Category: null, ImageUrl: "https://images.example.test/p.jpg", Price: 1.49m);
        CatalogReading.TryCreate(Store.Tesco, item, out var reading, out var reason).Should().BeTrue(reason);
        await _factory.WithDb(async db =>
        {
            await new CatalogRecorder(db, new PriceRecorder(db)).RecordAsync(Store.Tesco, [reading!], _factory.Clock.GetUtcNow(), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });
        var id = (await ReadAsync(await owner.PostAsJsonAsync("/api/products", new { name = "Dairy drink 2L" }))).GetProperty("id").GetGuid();
        Task<List<string>> ProposedAsync() => _factory.WithDb(db => db.MatchCandidates.AsNoTracking().Where(c => c.ProductId == id).Select(c => c.Url).ToListAsync());
        (await ProposedAsync()).Should().BeEmpty();

        // Adding the name looks again, so the proposal appears without waiting for a sweep.
        await AddAliasAsync(owner, id, "Whole milk 2L");

        (await ProposedAsync()).Should().ContainSingle().Which.Should().Be("https://www.tesco.ie/groceries/en-IE/products/254300011");
    }
}
