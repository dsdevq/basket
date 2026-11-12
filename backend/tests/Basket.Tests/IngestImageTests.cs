using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Ingest;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// The optional product photo address on a posted reading. A class of its own, so its factory (and the ingest rate
/// limit, 30 requests a minute per client) is not shared with <see cref="IngestTests"/>.
/// </summary>
public sealed class IngestImageTests : IClassFixture<BasketFactory>
{
    private const string TescoUrl = "https://www.tesco.ie/groceries/en-IE/products/254317390";
    private const string AldiUrl = "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541";

    private readonly BasketFactory _factory;

    public IngestImageTests(BasketFactory factory) => _factory = factory;

    /// <summary>Starts from no products, runs or tokens, with Milk linked to the Tesco and Aldi pages above.</summary>
    private async Task<HttpClient> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            await db.IngestTokens.ExecuteDeleteAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            db.Products.Add(product);
            db.StoreListings.AddRange(
                new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Tesco, Url = TescoUrl, AddedAt = DateTimeOffset.UtcNow },
                new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Aldi, Url = AldiUrl, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        return owner;
    }

    private async Task<HttpClient> RelayAsync()
    {
        var owner = await SeedAsync();
        var response = await owner.PostAsJsonAsync("/api/ingest-tokens", new { label = "Home PC" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, params object[] prices) =>
        client.PostAsJsonAsync("/api/ingest/prices", new { prices });

    private static object Tesco(decimal price) => new { store = "tesco", url = TescoUrl, price };

    private static string[] Reasons(JsonElement body) =>
        body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("reason").GetString() ?? "ok").ToArray();

    [Fact]
    public async Task A_reading_may_carry_the_photo_address_which_is_stored_and_never_cleared_by_one_without()
    {
        var relay = await RelayAsync();

        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.15m })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ImageAsync(TescoUrl)).Should().BeNull("no photo has been sent yet");

        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.15m, imageUrl = "https://digitalcontent.api.tesco.com/v2/media/ghs/a/b.jpeg?h=225" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ImageAsync(TescoUrl)).Should().Be("https://digitalcontent.api.tesco.com/v2/media/ghs/a/b.jpeg?h=225");

        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.20m, imageUrl = "  " })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(relay, Tesco(2.25m))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ImageAsync(TescoUrl)).Should().Be("https://digitalcontent.api.tesco.com/v2/media/ghs/a/b.jpeg?h=225", "a reading without a photo keeps the one stored");

        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.30m, imageUrl = "https://digitalcontent.api.tesco.com/v2/media/ghs/c/d.jpeg" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ImageAsync(TescoUrl)).Should().EndWith("c/d.jpeg", "a changed photo replaces it");
    }

    [Theory]
    [InlineData("http://img.example/a.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/a.png")]
    [InlineData("https://u:p@img.example/a.png")]
    [InlineData("https://img.example/a\nb.png")]
    public async Task A_photo_address_that_is_not_plain_https_is_ignored_the_price_is_kept_and_the_stored_photo_stays(string bad)
    {
        var relay = await RelayAsync();
        (await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.15m, imageUrl = "https://img.example/ok.png" })).EnsureSuccessStatusCode();

        var response = await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.99m, imageUrl = bad }, new { store = "aldi", url = AldiUrl, price = 1.19m });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Reasons(await response.Content.ReadFromJsonAsync<JsonElement>()).Should().Equal("ok", "ok");
        (await ImageAsync(TescoUrl)).Should().Be("https://img.example/ok.png");
        (await _factory.WithDb(db => db.PriceObservations.AnyAsync(o => o.Price == 2.99m))).Should().BeTrue();
    }

    [Fact]
    public async Task A_photo_address_over_the_cap_is_ignored_with_the_price_kept_and_at_the_cap_it_is_stored()
    {
        var relay = await RelayAsync();
        var atCap = ("https://img.example/" + new string('a', ImageUrls.MaxLength))[..ImageUrls.MaxLength];

        var tooLong = await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.10m, imageUrl = atCap + "a" });
        tooLong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ImageAsync(TescoUrl)).Should().BeNull();
        (await _factory.WithDb(db => db.PriceObservations.AnyAsync(o => o.Price == 2.10m))).Should().BeTrue();

        var fits = await PostAsync(relay, new { store = "tesco", url = TescoUrl, price = 2.15m, imageUrl = atCap });
        fits.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ImageAsync(TescoUrl)).Should().Be(atCap);
    }

    private Task<string?> ImageAsync(string url) =>
        _factory.WithDb(db => db.StoreListings.Where(l => l.Url == url).Select(l => l.ImageUrl).SingleAsync());
}
