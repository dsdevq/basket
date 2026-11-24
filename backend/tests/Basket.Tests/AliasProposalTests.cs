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
/// Pairs proposed from a Lidl Plus receipt that took over a tick-off trip: typed ticked lines against the receipt's own lines of the
/// same trip. Nothing is saved without an accept; a reject is remembered. Synthetic products and trips only.
/// </summary>
public sealed class AliasProposalTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public AliasProposalTests(BasketFactory factory) => _factory = factory;

    private async Task<(HttpClient Owner, Guid Eggs, Guid Milk)> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        return await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Trips.ExecuteDeleteAsync();
            await db.AliasRejections.ExecuteDeleteAsync();
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

    /// <summary>A receipt trip with the lines ticked off the list (typed ones have no product) and the receipt's own lines (a product, no shop of their own).</summary>
    private async Task AddReceiptTripAsync(int daysAgo, string[] typed, Guid[] receiptProducts, string[]? receiptOnlyText = null)
    {
        await _factory.WithDb(async db =>
        {
            var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).AddDays(-daysAgo);
            var trip = new Trip { Id = Guid.NewGuid(), ClosedAt = at, Source = TripSources.LidlPlus, ExternalId = Guid.NewGuid().ToString("N"), Total = 10m };
            db.Trips.Add(trip);
            foreach (var text in typed)
            {
                db.TripItems.Add(new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = text, CheckedAt = at, BoughtStore = Store.Lidl });
            }

            foreach (var product in receiptProducts)
            {
                var name = await db.Products.Where(p => p.Id == product).Select(p => p.Name).SingleAsync();
                db.TripItems.Add(new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = name, CheckedAt = at, ProductId = product, Store = Store.Lidl });
            }

            foreach (var text in receiptOnlyText ?? [])
            {
                db.TripItems.Add(new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = text, CheckedAt = at });
            }

            await db.SaveChangesAsync();
            return 0;
        });
    }

    private static async Task<JsonElement[]> ProposalsAsync(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/alias-proposals")).GetProperty("proposals").EnumerateArray().ToArray();

    [Fact]
    public async Task One_same_day_co_occurrence_proposes_the_pair()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddReceiptTripAsync(1, ["Яйця"], [eggs]);

        var proposals = await ProposalsAsync(owner);

        var proposal = proposals.Should().ContainSingle().Subject;
        proposal.GetProperty("text").GetString().Should().Be("Яйця");
        proposal.GetProperty("productId").GetGuid().Should().Be(eggs);
        proposal.GetProperty("productName").GetString().Should().Be("Free Range Eggs");
        proposal.GetProperty("trips").GetInt32().Should().Be(1);
        proposal.GetProperty("lastSeen").GetString().Should().Be("2026-11-19");
    }

    [Fact]
    public async Task Nothing_is_proposed_without_a_ticked_line_and_a_receipt_line_on_the_same_trip()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await AddReceiptTripAsync(1, [], [eggs]);
        await AddReceiptTripAsync(2, ["Яйця"], []);
        // A tick-off trip no receipt has taken over is not a receipt trip.
        await _factory.WithDb(async db =>
        {
            var trip = new Trip { Id = Guid.NewGuid(), ClosedAt = DateTimeOffset.UtcNow.AddDays(-3) };
            db.Trips.Add(trip);
            db.TripItems.Add(new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = "Яйця", CheckedAt = trip.ClosedAt });
            db.TripItems.Add(new TripItem { Id = Guid.NewGuid(), TripId = trip.Id, Text = "Whole milk 2L", CheckedAt = trip.ClosedAt, ProductId = milk });
            await db.SaveChangesAsync();
            return 0;
        });

        (await ProposalsAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_pair_seen_on_more_trips_counts_them_and_ranks_first()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await AddReceiptTripAsync(1, ["Яйця", "Молоко"], [eggs, milk]);
        await AddReceiptTripAsync(2, ["яйця  "], [eggs]);
        await AddReceiptTripAsync(3, ["Яйця"], [eggs]);

        var proposals = await ProposalsAsync(owner);

        proposals[0].GetProperty("productId").GetGuid().Should().Be(eggs);
        proposals[0].GetProperty("trips").GetInt32().Should().Be(3);
        proposals[0].GetProperty("text").GetString().Should().Be("Яйця", "the latest trip's words are shown");
        proposals.Select(p => (p.GetProperty("text").GetString(), p.GetProperty("trips").GetInt32())).Should().Contain(("Молоко", 1));
    }

    [Fact]
    public async Task Words_that_are_already_a_name_a_linked_tick_or_a_products_own_name_are_not_proposed()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await _factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            db.ProductAliases.Add(new ProductAlias { Id = Guid.NewGuid(), ProductId = milk, Text = "Молоко", NormalizedText = "молоко", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by });
            await db.SaveChangesAsync();
            return 0;
        });
        await AddReceiptTripAsync(1, ["Молоко", "free range eggs"], [eggs, milk]);

        // "Молоко" is taken; "free range eggs" is the eggs' own name, so it can only be proposed for the milk.
        var proposals = await ProposalsAsync(owner);

        proposals.Select(p => (p.GetProperty("text").GetString(), p.GetProperty("productId").GetGuid())).Should().Equal(("free range eggs", milk));
    }

    [Fact]
    public async Task Accept_saves_the_name_as_an_alias_and_the_pair_stops_being_proposed()
    {
        var (owner, eggs, _) = await SeedAsync();
        await AddReceiptTripAsync(1, ["Яйця"], [eggs]);
        (await ProposalsAsync(owner)).Should().HaveCount(1);
        (await _factory.WithDb(async db => await db.ProductAliases.CountAsync())).Should().Be(0, "nothing is saved without a tap");

        var response = await owner.PostAsJsonAsync("/api/alias-proposals/accept", new { text = "Яйця", productId = eggs });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await _factory.WithDb(async db => await db.ProductAliases.Where(a => a.ProductId == eggs).Select(a => a.Text).ToListAsync())).Should().Equal("Яйця");
        (await ProposalsAsync(owner)).Should().BeEmpty();
        // The typed words now link a new line to the product by themselves.
        var line = await owner.PostAsJsonAsync("/api/list/items", new { text = "Яйця" });
        (await line.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("productId").GetGuid().Should().Be(eggs);
    }

    [Fact]
    public async Task Accept_refuses_a_pair_that_is_not_proposed()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await AddReceiptTripAsync(1, ["Яйця"], [eggs]);

        var response = await owner.PostAsJsonAsync("/api/alias-proposals/accept", new { text = "Яйця", productId = milk });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PostAsJsonAsync("/api/alias-proposals/accept", new { text = " ", productId = eggs })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.WithDb(async db => await db.ProductAliases.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Reject_is_remembered_and_the_pair_is_not_proposed_again_even_on_later_trips()
    {
        var (owner, eggs, milk) = await SeedAsync();
        await AddReceiptTripAsync(1, ["Яйця"], [eggs, milk]);

        (await owner.PostAsJsonAsync("/api/alias-proposals/reject", new { text = "яйця", productId = eggs })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync("/api/alias-proposals/reject", new { text = "Яйця", productId = eggs })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AddReceiptTripAsync(2, ["Яйця"], [eggs]);

        var proposals = await ProposalsAsync(owner);

        proposals.Should().ContainSingle("only the rejected pair is gone; the same words for another product still are proposed")
            .Which.GetProperty("productId").GetGuid().Should().Be(milk);
        (await _factory.WithDb(async db => await db.AliasRejections.CountAsync())).Should().Be(1);
        (await _factory.WithDb(async db => await db.ProductAliases.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_endpoints_take_a_signed_in_person_only()
    {
        await SeedAsync();
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync("/api/alias-proposals")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
