using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// <c>GET /api/promotions/usual</c>: offers on the products the household usually buys, read from the whole catalog and not only from
/// linked pages. Against Postgres, every product invented.
/// </summary>
public sealed class UsualOffersTests
{
    private static DateOnly Today(BasketFactory factory) => PriceRecorder.DublinDay(factory.Clock.GetUtcNow());

    private static async Task<Guid> ProductAsync(BasketFactory factory, string name) =>
        await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product.Id;
        });

    /// <summary>Two closed trips that bought each product: that is "usual".</summary>
    private static async Task BuyUsuallyAsync(BasketFactory factory, HttpClient client, params Guid[] products)
    {
        for (var trip = 0; trip < 2; trip++)
        {
            foreach (var product in products)
            {
                var added = await client.PostAsJsonAsync("/api/list/items", new { productId = product, store = (string?)null });
                var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                (await client.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = true })).StatusCode.Should().Be(HttpStatusCode.OK);
            }

            (await client.PostAsync("/api/list/close", null)).StatusCode.Should().Be(HttpStatusCode.Created);
            factory.Clock.Advance(TimeSpan.FromDays(1));
        }
    }

    private static async Task<StoreProduct> CatalogAsync(
        BasketFactory factory,
        Store store,
        string name,
        string sizeText,
        decimal amount,
        string unit,
        decimal price,
        decimal? was = null,
        string? promo = null,
        bool inStock = true,
        int seenDaysAgo = 0)
    {
        var row = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Store = store,
            ExternalId = Guid.NewGuid().ToString("N")[..12],
            Url = $"https://{store.Host()}/p/{Guid.NewGuid():N}",
            Name = name,
            SizeText = sizeText,
            SizeAmount = amount,
            SizeUnit = unit,
            SizePacks = 1,
            FirstSeenAt = factory.Clock.GetUtcNow().AddDays(-30),
            LastSeenAt = factory.Clock.GetUtcNow().AddDays(-seenDaysAgo),
        };
        await factory.WithDb(async db =>
        {
            db.StoreProducts.Add(row);
            db.StoreProductPrices.Add(new StoreProductPrice { StoreProductId = row.Id, ObservedOn = Today(factory), Price = price, WasPrice = was, PromoText = promo, InStock = inStock });
            await db.SaveChangesAsync();
            return 0;
        });
        return row;
    }

    private static async Task<JsonElement[]> UsualAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/promotions/usual")).GetProperty("products").EnumerateArray().ToArray();

    [Fact]
    public async Task Needs_a_signed_in_person()
    {
        await using var factory = new BasketFactory();

        (await factory.CreateClient().GetAsync("/api/promotions/usual")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_usual_product_shows_an_offer_in_a_shop_where_it_has_no_linked_page()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        await BuyUsuallyAsync(factory, owner, milk);
        await CatalogAsync(factory, Store.Lidl, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, 1.49m, was: 1.99m);

        var products = await UsualAsync(owner);

        var product = products.Should().ContainSingle().Subject;
        product.GetProperty("name").GetString().Should().Be("Whole milk 2L");
        var offer = product.GetProperty("offers").EnumerateArray().Single();
        offer.GetProperty("offer").GetProperty("store").GetString().Should().Be("lidl");
        offer.GetProperty("offer").GetProperty("isPromo").GetBoolean().Should().BeTrue();
        offer.GetProperty("offer").GetProperty("eachPrice").GetDecimal().Should().Be(1.49m);
        offer.GetProperty("offer").GetProperty("referencePrice").GetDecimal().Should().Be(1.99m);
    }

    [Fact]
    public async Task Every_current_offer_on_a_usual_product_is_shown_not_only_the_first_few()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        await BuyUsuallyAsync(factory, owner, milk);
        foreach (var price in new[] { 1.19m, 1.29m, 1.39m, 1.49m })
        {
            await CatalogAsync(factory, Store.Lidl, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, price, was: 1.99m);
        }

        var product = (await UsualAsync(owner)).Should().ContainSingle().Subject;

        product.GetProperty("offers").EnumerateArray().Select(o => o.GetProperty("offer").GetProperty("eachPrice").GetDecimal())
            .Should().Equal(1.19m, 1.29m, 1.39m, 1.49m);
    }

    [Fact]
    public async Task A_product_bought_once_is_not_usual_so_its_offers_are_not_shown()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        var added = await owner.PostAsJsonAsync("/api/list/items", new { productId = milk, store = (string?)null });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = true });
        await owner.PostAsync("/api/list/close", null);
        await CatalogAsync(factory, Store.Lidl, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, 1.49m, was: 1.99m);

        (await UsualAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_promotions_that_are_current_and_in_stock_are_shown()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        await BuyUsuallyAsync(factory, owner, milk);
        await CatalogAsync(factory, Store.Lidl, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, 1.99m);
        await CatalogAsync(factory, Store.Aldi, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, 1.49m, was: 1.99m, inStock: false);
        await CatalogAsync(factory, Store.Tesco, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, 1.49m, was: 1.99m, seenDaysAgo: 10);

        (await UsualAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_the_household_rejected_is_never_shown()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        await BuyUsuallyAsync(factory, owner, milk);
        var row = await CatalogAsync(factory, Store.Lidl, "Whole milk", "2 l", 2000, StoreProduct.Millilitres, 1.49m, was: 1.99m);
        await factory.WithDb(async db =>
        {
            db.MatchCandidates.Add(new MatchCandidate { Id = Guid.NewGuid(), ProductId = milk, Store = Store.Lidl, Url = row.Url, Basis = "name_size", Score = 60, Status = CandidateStatus.Rejected, ProposedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        (await UsualAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_of_another_product_is_not_shown()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var milk = await ProductAsync(factory, "Whole milk 2L");
        await BuyUsuallyAsync(factory, owner, milk);
        await CatalogAsync(factory, Store.Lidl, "Orange juice", "2 l", 2000, StoreProduct.Millilitres, 1.49m, was: 1.99m);

        (await UsualAsync(owner)).Should().BeEmpty();
    }
}
