using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The optional category on a posted reading. Its own fixture: <see cref="IngestTests"/> already uses most of the ingest rate limit.</summary>
public sealed class IngestCategoryTests : IClassFixture<BasketFactory>
{
    private const string TescoUrl = "https://www.tesco.ie/groceries/en-IE/products/254317390";

    private readonly BasketFactory _factory;

    public IngestCategoryTests(BasketFactory factory) => _factory = factory;

    private async Task<(HttpClient Owner, HttpClient Relay)> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            await db.IngestTokens.ExecuteDeleteAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            db.Products.Add(product);
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Tesco, Url = TescoUrl, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        var minted = await (await owner.PostAsJsonAsync("/api/ingest-tokens", new { label = "Home PC" })).Content.ReadFromJsonAsync<JsonElement>();
        var relay = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        relay.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.GetProperty("token").GetString());
        return (owner, relay);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient relay, params object[] prices) =>
        relay.PostAsJsonAsync("/api/ingest/prices", new { prices });

    [Fact]
    public async Task A_category_is_stored_on_the_listing_and_a_later_reading_without_one_keeps_it()
    {
        var (owner, relay) = await SeedAsync();

        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.15m, category = "Fresh Food > Milk" })).EnsureSuccessStatusCode();
        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.20m })).EnsureSuccessStatusCode();
        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.25m, category = "   " })).EnsureSuccessStatusCode();

        (await _factory.WithDb(db => db.StoreListings.Select(l => l.Category).SingleAsync())).Should().Be("Fresh Food > Milk");
        var products = await owner.GetFromJsonAsync<JsonElement>("/api/products");
        var listing = products.GetProperty("products")[0].GetProperty("stores").EnumerateArray()
            .Single(s => s.GetProperty("store").GetString() == "tesco").GetProperty("listing");
        listing.GetProperty("category").GetString().Should().Be("Fresh Food > Milk");
    }

    [Fact]
    public async Task A_category_is_cleaned_and_cut_to_the_stored_length()
    {
        var (_, relay) = await SeedAsync();

        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.15m, category = "  Fresh   Food >   " + new string('x', 400) })).EnsureSuccessStatusCode();

        var stored = await _factory.WithDb(db => db.StoreListings.Select(l => l.Category).SingleAsync());
        stored.Should().StartWith("Fresh Food > xxx").And.HaveLength(StoreListing.MaxCategoryLength);
    }
}
