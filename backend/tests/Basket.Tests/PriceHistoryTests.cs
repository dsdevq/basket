using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Promotions;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The 90-day history and the "lowest in 90 days" mark: the rule on in-memory readings, and the route that serves it.</summary>
public sealed class PriceHistoryTests : IClassFixture<BasketFactory>
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private readonly BasketFactory _factory;

    public PriceHistoryTests(BasketFactory factory) => _factory = factory;

    private static PriceObservation Read(int daysAgo, decimal? price, decimal? was = null, string? promo = null, decimal? loyalty = null) => new()
    {
        Id = Guid.NewGuid(),
        ListingId = Guid.Empty,
        ObservedOn = Today.AddDays(-daysAgo),
        Price = price,
        WasPrice = was,
        PromoText = promo,
        LoyaltyPrice = loyalty,
        LoyaltyScheme = loyalty is null ? null : "Clubcard",
        InStock = true,
    };

    [Fact]
    public void The_cheapest_price_of_the_window_that_was_dearer_before_is_the_lowest()
    {
        var window = new[] { Read(60, 2.50m), Read(30, 2.20m), Read(0, 1.80m, was: 2.50m) };

        PriceHistory.IsLowest(1.80m, window, Today).Should().BeTrue();
    }

    [Fact]
    public void A_price_that_was_lower_inside_the_window_is_not_the_lowest()
    {
        var window = new[] { Read(80, 1.50m), Read(40, 2.50m), Read(0, 1.80m, was: 2.50m) };

        PriceHistory.IsLowest(1.80m, window, Today).Should().BeFalse();
    }

    [Fact]
    public void A_price_lower_only_outside_the_window_still_counts()
    {
        var window = new[] { Read(91, 1.00m), Read(45, 2.50m), Read(0, 1.80m) };

        PriceHistory.IsLowest(1.80m, window, Today).Should().BeTrue();
    }

    [Fact]
    public void A_price_that_never_changed_is_not_called_the_lowest()
    {
        PriceHistory.IsLowest(2.00m, [Read(40, 2.00m), Read(0, 2.00m)], Today).Should().BeFalse();
        PriceHistory.IsLowest(2.00m, [], Today).Should().BeFalse();
    }

    [Fact]
    public void A_price_equal_to_the_lowest_is_the_lowest_when_it_was_dearer_in_between()
    {
        var window = new[] { Read(70, 1.80m), Read(40, 2.40m), Read(0, 1.80m) };

        PriceHistory.IsLowest(1.80m, window, Today).Should().BeTrue();
    }

    [Fact]
    public void Prices_are_judged_as_what_a_pack_costs_with_the_offer_and_the_card()
    {
        // Shelf 2.40; card price 1.90 today: 1.90 is what the card holder pays and it beats every earlier effective price.
        var window = new[] { Read(50, 2.40m), Read(20, 2.20m, promo: "2 for €4"), Read(0, 2.40m, loyalty: 1.90m) };

        PriceHistory.Effective(window[1]).Should().Be(2.00m);
        PriceHistory.IsLowest(1.90m, window, Today).Should().BeTrue();
    }

    [Fact]
    public void The_history_lists_the_window_oldest_first_with_the_extremes_and_leaves_out_older_readings()
    {
        var response = PriceHistory.Build(
            Guid.NewGuid(),
            Store.Lidl,
            [Read(0, 1.80m, was: 2.50m), Read(120, 0.50m), Read(60, 2.50m), Read(30, null)],
            Today);

        response.Points.Select(p => p.ObservedOn).Should().Equal(Today.AddDays(-60), Today.AddDays(-30), Today);
        response.Points[1].Effective.Should().BeNull();
        response.Lowest.Should().Be(new HistoryExtreme(1.80m, Today));
        response.Highest.Should().Be(new HistoryExtreme(2.50m, Today.AddDays(-60)));
        response.Current.Should().Be(1.80m);
        response.LowestIn90Days.Should().BeTrue();
        response.From.Should().Be(Today.AddDays(-89));
    }

    [Fact]
    public void The_promotions_mark_a_promo_that_is_the_lowest_and_never_a_plain_price()
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Butter 250g", AddedAt = DateTimeOffset.UtcNow };
        var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Lidl, Url = "https://example.test/x", Title = "Butter 250g" };
        var earlier = Read(40, 2.50m);
        earlier.ListingId = listing.Id;
        var now = Read(0, 1.80m, was: 2.50m);
        now.ListingId = listing.Id;

        var marked = PromotionBuilder.Build([product], new[] { listing }.ToLookup(l => l.ProductId), new Dictionary<Guid, PriceObservation> { [listing.Id] = now }, Today, new[] { earlier, now }.ToLookup(o => o.ListingId));
        var unmarked = PromotionBuilder.Build([product], new[] { listing }.ToLookup(l => l.ProductId), new Dictionary<Guid, PriceObservation> { [listing.Id] = now }, Today);

        marked.Products.Single().Offers.Single().LowestIn90Days.Should().BeTrue();
        unmarked.Products.Single().Offers.Single().LowestIn90Days.Should().BeFalse();
    }

    [Fact]
    public async Task The_history_route_serves_a_signed_in_person_and_refuses_others()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var today = PriceRecorder.DublinDay(_factory.Clock.GetUtcNow());
        var (productId, _) = await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk 1L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Lidl, Url = "https://www.lidl.ie/p/milk/p11000", AddedAt = DateTimeOffset.UtcNow };
            db.Products.Add(product);
            db.StoreListings.Add(listing);
            foreach (var (daysAgo, price) in new[] { (100, 0.80m), (20, 1.29m), (0, 1.09m) })
            {
                db.PriceObservations.Add(new PriceObservation
                {
                    Id = Guid.NewGuid(),
                    ListingId = listing.Id,
                    ObservedOn = today.AddDays(-daysAgo),
                    ObservedAt = _factory.Clock.GetUtcNow().AddDays(-daysAgo),
                    Price = price,
                    InStock = true,
                });
            }

            await db.SaveChangesAsync();
            return (product.Id, 0);
        });

        var history = await owner.GetFromJsonAsync<JsonElement>($"/api/products/{productId}/stores/lidl/history");

        history.GetProperty("days").GetInt32().Should().Be(90);
        history.GetProperty("points").GetArrayLength().Should().Be(2);
        history.GetProperty("lowest").GetProperty("price").GetDecimal().Should().Be(1.09m);
        history.GetProperty("highest").GetProperty("price").GetDecimal().Should().Be(1.29m);
        history.GetProperty("lowestIn90Days").GetBoolean().Should().BeTrue();

        (await owner.GetAsync($"/api/products/{productId}/stores/aldi/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync($"/api/products/{productId}/stores/nowhere/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.NewBrowser().GetAsync($"/api/products/{productId}/stores/lidl/history")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
