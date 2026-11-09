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

/// <summary>The ranking across shops, on in-memory data, and the endpoint that serves it.</summary>
public sealed class PromotionsViewTests : IClassFixture<BasketFactory>
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private readonly BasketFactory _factory;

    public PromotionsViewTests(BasketFactory factory) => _factory = factory;

    private sealed class Shelf
    {
        public List<Product> Products { get; } = [];

        public List<StoreListing> Listings { get; } = [];

        public Dictionary<Guid, PriceObservation> Latest { get; } = [];

        public Product Product(string name)
        {
            var product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = DateTimeOffset.UtcNow };
            Products.Add(product);
            return product;
        }

        public void Read(Product product, Store store, string? title, Action<PriceObservation> reading, DateOnly? observedOn = null)
        {
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = store, Url = "https://example.test/" + Guid.NewGuid(), Title = title };
            Listings.Add(listing);
            var observation = new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = observedOn ?? Today, InStock = true };
            reading(observation);
            Latest[listing.Id] = observation;
        }

        public void Linked(Product product, Store store) =>
            Listings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = store, Url = "https://example.test/" + Guid.NewGuid() });

        public PromotionsResponse Build() => PromotionBuilder.Build(Products, Listings.ToLookup(l => l.ProductId), Latest, Today);
    }

    private static PromoOffer Of(PromoProduct product, string store) => product.Offers.Single(o => o.Store == store);

    [Fact]
    public void Promotions_of_different_kinds_and_pack_sizes_are_ranked_on_one_unit_price()
    {
        var shelf = new Shelf();
        var chicken = shelf.Product("Chicken fillets");
        // Lidl: 2 for €5 on a 500 g pack = 2.50 each = €5.00/kg.
        shelf.Read(chicken, Store.Lidl, "Chicken fillets 500g", o => { o.Price = 3.00m; o.PromoText = "2 for €5"; });
        // Aldi: was 8.00, now 6.00 on 1 kg = €6.00/kg.
        shelf.Read(chicken, Store.Aldi, "Aldi chicken fillets 1kg", o => { o.Price = 6.00m; o.WasPrice = 8.00m; });
        // Tesco: Clubcard 3.20 on 650 g = 4.923/kg -> €4.92/kg, cheaper than the Lidl multi-buy.
        shelf.Read(chicken, Store.Tesco, "Tesco chicken 650g", o => { o.Price = 4.00m; o.LoyaltyPrice = 3.20m; o.LoyaltyScheme = "Clubcard"; });

        var product = shelf.Build().Products.Single();

        product.Unit.Should().Be("kg");
        product.Ranked.Should().Be(3);
        product.Offers.Select(o => (o.Store, o.Rank)).Should().Equal(("tesco", 1), ("lidl", 2), ("aldi", 3));
        Of(product, "tesco").UnitPrice.Should().Be(4.92m);
        Of(product, "tesco").Card.Should().Be("Clubcard");
        Of(product, "tesco").Kind.Should().Be("loyalty");
        Of(product, "lidl").UnitPrice.Should().Be(5.00m);
        Of(product, "lidl").Kind.Should().Be("multi_buy");
        Of(product, "lidl").BuyQuantity.Should().Be(2);
        Of(product, "aldi").UnitPrice.Should().Be(6.00m);
        Of(product, "aldi").Kind.Should().Be("percentage");
        Of(product, "aldi").ReferenceUnitPrice.Should().Be(8.00m);
    }

    [Fact]
    public void A_shelf_price_in_another_shop_is_ranked_beside_the_promos_so_the_comparison_is_honest()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Milk");
        shelf.Read(milk, Store.Lidl, "Milk 2L", o => { o.Price = 2.40m; o.WasPrice = 3.00m; }); // promo, €1.20/l
        shelf.Read(milk, Store.Aldi, "Milk 1L", o => o.Price = 1.00m); // no promo, but €1.00/l is cheaper

        var product = shelf.Build().Products.Single();

        Of(product, "aldi").IsPromo.Should().BeFalse();
        Of(product, "aldi").Rank.Should().Be(1);
        Of(product, "lidl").Rank.Should().Be(2);
    }

    [Fact]
    public void A_product_with_no_promo_anywhere_is_not_listed()
    {
        var shelf = new Shelf();
        var butter = shelf.Product("Butter 250g");
        shelf.Read(butter, Store.Lidl, null, o => o.Price = 2.20m);
        shelf.Read(butter, Store.Aldi, null, o => o.Price = 2.30m);

        shelf.Build().Products.Should().BeEmpty();
    }

    [Fact]
    public void A_stale_or_out_of_stock_reading_is_flagged_and_left_out_of_the_ranking()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        shelf.Read(rice, Store.Lidl, null, o => { o.Price = 2.00m; o.WasPrice = 2.50m; });
        shelf.Read(rice, Store.Aldi, null, o => { o.Price = 0.50m; o.WasPrice = 1.00m; }, observedOn: Today.AddDays(-(PromotionBuilder.StaleAfterDays + 1)));
        shelf.Read(rice, Store.Tesco, null, o => { o.Price = 0.40m; o.WasPrice = 1.00m; o.InStock = false; });

        var product = shelf.Build().Products.Single();

        product.Ranked.Should().Be(1);
        Of(product, "lidl").Rank.Should().Be(1);
        Of(product, "aldi").Rank.Should().BeNull();
        Of(product, "aldi").Flags.Should().Contain(PromotionBuilder.FlagStale);
        Of(product, "aldi").ObservedOn.Should().Be(Today.AddDays(-4), "a price carries its date");
        Of(product, "tesco").Rank.Should().BeNull();
        Of(product, "tesco").Flags.Should().Contain(PromotionBuilder.FlagOutOfStock);
    }

    [Fact]
    public void A_reading_exactly_at_the_staleness_limit_still_counts()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        shelf.Read(rice, Store.Lidl, null, o => { o.Price = 2.00m; o.WasPrice = 2.50m; }, observedOn: Today.AddDays(-PromotionBuilder.StaleAfterDays));

        Of(shelf.Build().Products.Single(), "lidl").Flags.Should().NotContain(PromotionBuilder.FlagStale);
    }

    [Fact]
    public void A_missing_size_is_flagged_and_the_offer_has_no_unit_price()
    {
        var shelf = new Shelf();
        var skewers = shelf.Product("Chicken skewers");
        shelf.Read(skewers, Store.Lidl, "VITASIA 10 Chicken Breast Skewers", o => { o.Price = 2.19m; o.PromoText = "2 for €4"; });

        var offer = Of(shelf.Build().Products.Single(), "lidl");

        offer.UnitPrice.Should().BeNull();
        offer.Unit.Should().BeNull();
        offer.Rank.Should().BeNull();
        offer.Flags.Should().Contain(PromotionBuilder.FlagNoSize);
        offer.EachPrice.Should().Be(2.00m, "the price per pack is still known");
    }

    [Fact]
    public void A_size_in_the_household_name_is_used_when_the_shops_title_has_none_and_says_so()
    {
        var shelf = new Shelf();
        var milk = shelf.Product("Whole milk 2L");
        shelf.Read(milk, Store.Aldi, "Aldi milk", o => { o.Price = 2.00m; o.WasPrice = 2.50m; });

        var offer = Of(shelf.Build().Products.Single(), "aldi");

        offer.UnitPrice.Should().Be(1.00m);
        offer.Flags.Should().Contain(PromotionBuilder.FlagSizeFromName);
    }

    [Fact]
    public void Packs_measured_in_different_kinds_of_unit_are_not_compared()
    {
        var shelf = new Shelf();
        var eggs = shelf.Product("Eggs");
        shelf.Read(eggs, Store.Lidl, "Free range eggs 6 pack", o => { o.Price = 2.40m; o.WasPrice = 3.00m; }); // €0.40 each
        shelf.Read(eggs, Store.Aldi, "Free range eggs pack of 12", o => { o.Price = 3.60m; o.WasPrice = 4.00m; }); // €0.30 each
        shelf.Read(eggs, Store.Tesco, "Eggs 700g", o => { o.Price = 1.00m; o.WasPrice = 2.00m; }); // kg: not comparable with pieces

        var product = shelf.Build().Products.Single();

        product.Unit.Should().Be("each");
        Of(product, "aldi").Rank.Should().Be(1);
        Of(product, "lidl").Rank.Should().Be(2);
        Of(product, "tesco").Rank.Should().BeNull();
        Of(product, "tesco").Flags.Should().Contain(PromotionBuilder.FlagOtherUnit);
    }

    [Fact]
    public void Equal_unit_prices_share_a_place()
    {
        var shelf = new Shelf();
        var oil = shelf.Product("Olive oil 1L");
        shelf.Read(oil, Store.Lidl, null, o => { o.Price = 5.00m; o.WasPrice = 6.00m; });
        shelf.Read(oil, Store.Aldi, null, o => { o.Price = 5.00m; o.WasPrice = 7.00m; });
        shelf.Read(oil, Store.Tesco, null, o => { o.Price = 6.00m; o.WasPrice = 7.00m; });

        var product = shelf.Build().Products.Single();

        product.Offers.Select(o => (o.Store, o.Rank)).Should().BeEquivalentTo(new[] { ("lidl", (int?)1), ("aldi", 1), ("tesco", 3) });
    }

    [Fact]
    public void Shops_with_nothing_usable_today_are_named_with_the_reason_never_guessed()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        shelf.Read(rice, Store.Lidl, null, o => { o.Price = 2.00m; o.WasPrice = 2.50m; });
        shelf.Read(rice, Store.Aldi, null, o => { o.Price = 1.00m; o.ValidFrom = Today.AddDays(2); });
        shelf.Read(rice, Store.Tesco, null, o => { o.Price = 1.00m; o.ValidUntil = Today.AddDays(-1); });
        shelf.Read(rice, Store.Dunnes, null, o => { });
        var other = shelf.Product("Beans");
        shelf.Linked(other, Store.Lidl);

        var product = shelf.Build().Products.Single();

        product.Gaps.Select(g => (g.Store, g.Reason)).Should().Equal(("aldi", "upcoming"), ("tesco", "ended"), ("dunnes", "no_price"));
        product.Gaps.Single(g => g.Store == "aldi").Date.Should().Be(Today.AddDays(2));
        product.Gaps.Single(g => g.Store == "tesco").Date.Should().Be(Today.AddDays(-1));
    }

    [Fact]
    public void A_linked_shop_with_no_reading_yet_is_a_gap()
    {
        var shelf = new Shelf();
        var rice = shelf.Product("Rice 1kg");
        shelf.Read(rice, Store.Lidl, null, o => { o.Price = 2.00m; o.WasPrice = 2.50m; });
        shelf.Linked(rice, Store.Tesco);

        shelf.Build().Products.Single().Gaps.Should().ContainSingle().Which.Should().Be(new PromoGap("tesco", "Tesco", "not_read", null));
    }

    [Fact]
    public async Task The_endpoint_serves_the_ranking_to_a_signed_in_person_only()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk 1L", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = user };
            var listing = new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Lidl, Url = "https://www.lidl.ie/p/milk/p11000", AddedAt = DateTimeOffset.UtcNow };
            db.Products.Add(product);
            db.StoreListings.Add(listing);
            db.PriceObservations.Add(new PriceObservation
            {
                Id = Guid.NewGuid(),
                ListingId = listing.Id,
                ObservedOn = PriceRecorder.DublinDay(_factory.Clock.GetUtcNow()),
                ObservedAt = _factory.Clock.GetUtcNow(),
                Price = 1.29m,
                LoyaltyPrice = 0.99m,
                LoyaltyScheme = "Lidl Plus",
                InStock = true,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var view = await owner.GetFromJsonAsync<JsonElement>("/api/promotions");

        view.GetProperty("staleAfterDays").GetInt32().Should().Be(PromotionBuilder.StaleAfterDays);
        var offer = view.GetProperty("products").EnumerateArray().Single().GetProperty("offers").EnumerateArray().Single();
        offer.GetProperty("store").GetString().Should().Be("lidl");
        offer.GetProperty("kind").GetString().Should().Be("loyalty");
        offer.GetProperty("card").GetString().Should().Be("Lidl Plus");
        offer.GetProperty("unit").GetString().Should().Be("l");
        offer.GetProperty("unitPrice").GetDecimal().Should().Be(0.99m);
        offer.GetProperty("rank").GetInt32().Should().Be(1);

        (await _factory.NewBrowser().GetAsync("/api/promotions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
