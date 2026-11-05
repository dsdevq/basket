using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>The shared write path: what any reader (the nightly job, a later PC ingest) does to the history.</summary>
public sealed class PriceRecorderTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public PriceRecorderTests(BasketFactory factory) => _factory = factory;

    private async Task<Guid> SeedListingAsync()
    {
        await _factory.OwnerAsync();
        return await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Tesco, Url = "https://www.tesco.ie/groceries/en-IE/products/254317390", AddedAt = DateTimeOffset.UtcNow };
            db.Products.Add(product);
            db.StoreListings.Add(listing);
            await db.SaveChangesAsync();
            return listing.Id;
        });
    }

    private static PriceQuote Quote(decimal price, string? title = "Tesco Milk") =>
        new(title, price, null, price - 0.2m, "Clubcard", "Clubcard Price", null, null, true);

    [Fact]
    public async Task A_reading_from_any_source_lands_as_the_days_observation_and_updates_the_listing()
    {
        var id = await SeedListingAsync();

        await _factory.WithDb(async db =>
        {
            var listing = await db.StoreListings.SingleAsync(l => l.Id == id);
            var recorder = new PriceRecorder(db);
            await recorder.RecordAsync(listing, Quote(1.80m), DateTimeOffset.Parse("2026-11-05T20:00:00Z"), CancellationToken.None);
            await db.SaveChangesAsync();
            await recorder.RecordAsync(listing, Quote(1.70m, title: null), DateTimeOffset.Parse("2026-11-05T22:00:00Z"), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

        await _factory.WithDb(async db =>
        {
            var observation = await db.PriceObservations.SingleAsync(o => o.ListingId == id);
            observation.ObservedOn.Should().Be(new DateOnly(2026, 11, 5));
            observation.Price.Should().Be(1.70m);
            observation.LoyaltyPrice.Should().Be(1.50m);
            observation.LoyaltyScheme.Should().Be("Clubcard");
            var listing = await db.StoreListings.SingleAsync(l => l.Id == id);
            listing.Title.Should().Be("Tesco Milk", "a reading without a title keeps the known one");
            listing.LastError.Should().BeNull();
            return 0;
        });
    }

    [Fact]
    public async Task A_failed_reading_marks_the_listing_and_leaves_the_history_alone()
    {
        var id = await SeedListingAsync();
        await _factory.WithDb(async db =>
        {
            var listing = await db.StoreListings.SingleAsync(l => l.Id == id);
            await new PriceRecorder(db).RecordAsync(listing, Quote(1.80m), DateTimeOffset.Parse("2026-11-05T20:00:00Z"), CancellationToken.None);
            PriceRecorder.RecordFailure(listing, new string('x', 900), DateTimeOffset.Parse("2026-11-06T20:00:00Z"));
            await db.SaveChangesAsync();
            return 0;
        });

        await _factory.WithDb(async db =>
        {
            (await db.PriceObservations.CountAsync(o => o.ListingId == id)).Should().Be(1);
            var listing = await db.StoreListings.SingleAsync(l => l.Id == id);
            listing.LastError.Should().HaveLength(500);
            listing.LastCheckedAt.Should().Be(DateTimeOffset.Parse("2026-11-06T20:00:00Z"));
            return 0;
        });
    }
}
