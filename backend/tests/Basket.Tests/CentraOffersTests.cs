using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;
using Basket.Api.Prices.Promotions;
using Basket.Api.Prices.Sources;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Basket.Tests;

/// <summary>
/// Centra has no catalogue, only the weekly Special Offers page. These tests read a saved copy of it (no network): a price, a
/// multibuy and an offer with no usable name, then the offers' way through the catalog, matching and the promotions.
/// </summary>
public sealed class CentraOffersTests : IClassFixture<BasketFactory>
{
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static readonly DateTimeOffset Noon = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly BasketFactory _factory;

    public CentraOffersTests(BasketFactory factory) => _factory = factory;

    private static string Page => Fixtures.Read("centra-offers-page.html");

    private static CentraOffers Offers(DateOnly? today = null) => CentraParser.Parse(Page, today ?? Today);

    // ---- the page ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_priced_offer_is_read_with_its_name_category_and_valid_until_date()
    {
        var beer = Offers().Items.Single(i => i.ExternalId == "asahi-super-dry-bottle-620ml");

        beer.Should().BeEquivalentTo(new CatalogItem(
            "asahi-super-dry-bottle-620ml",
            "https://centra.ie/offers/asahi-super-dry-bottle-620ml",
            "Asahi Super Dry Beer Bottle 620ml",
            Category: "Beer, Wine & Spirits",
            ImageUrl: beer.ImageUrl,
            Price: 3.50m,
            PromoText: "Weekly offer",
            ValidUntil: new DateOnly(2026, 11, 25)));
        beer.ImageUrl.Should().StartWith("https://centra.ie/thumbnail/");
    }

    [Fact]
    public void A_multibuy_offer_keeps_its_deal_as_promo_text_and_the_single_price_when_the_tile_shows_one()
    {
        var items = Offers().Items;

        var lasagne = items.Single(i => i.Name!.StartsWith("The Happy Pear", StringComparison.Ordinal));
        lasagne.Price.Should().Be(6m);
        lasagne.PromoText.Should().Be("Weekly offer: 2 for €11");
        PromoText.ParseMultiBuy(lasagne.PromoText).Should().NotBeNull().And.Match<MultiBuy>(m => m.Quantity == 2 && m.Total == 11m);

        var vols = items.Single(i => i.Name!.StartsWith("Centra Chicken", StringComparison.Ordinal));
        vols.Name.Should().Be("Centra Chicken & Mushroom Vols Au Vent 252g");
        vols.Price.Should().Be(4m);
        PromoText.ParseMultiBuy(vols.PromoText).Should().Match<MultiBuy>(m => m.Quantity == 3 && m.Total == 10m);

        PromoText.ParseMultiBuy(items.Single(i => i.Name!.StartsWith("Heinz", StringComparison.Ordinal)).PromoText)
            .Should().Match<MultiBuy>(m => m.Quantity == 4 && m.Total == 5m);
    }

    [Fact]
    public void An_offer_with_no_usable_name_is_counted_and_skipped_never_guessed()
    {
        var offers = Offers();

        offers.Unusable.Should().Be(1);
        offers.Items.Should().HaveCount(5).And.NotContain(i => i.ExternalId == "mystery-multipack");
        offers.Items.Should().OnlyContain(i => i.ValidUntil != null && i.PromoText!.StartsWith(CentraParser.PromoPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("99c", "")]
    [InlineData("€3,50", "")]
    [InlineData("3 for €10", "")]
    [InlineData("Half price", "")]
    public void A_tile_whose_price_cannot_be_read_is_counted_never_recorded_without_one(string message, string each)
    {
        var tile = $"""<a href="/offers/odd-one" class="item product-id-1"><p class="message">{message}</p><p class="price ">{each}</p><h4 class="product-name"><span>Odd One 1kg</span></h4><p class="offer-valid">Offer valid until <span class="date">25th November</span></p></a>""";

        var offers = CentraParser.Parse(tile, Today);

        offers.Items.Should().BeEmpty();
        offers.Unusable.Should().Be(1);
    }

    [Theory]
    [InlineData("2026-11-19", "2026-11-25")]
    [InlineData("2026-11-25", "2026-11-25")]
    [InlineData("2026-11-26", "2026-11-25")]
    [InlineData("2027-05-23", "2026-11-25")]
    [InlineData("2027-06-01", "2027-11-25")]
    public void The_year_less_date_is_read_as_the_day_of_that_name_nearest_to_today(string readOn, string validUntil)
    {
        Offers(DateOnly.Parse(readOn)).Items.Single(i => i.ExternalId == "asahi-super-dry-bottle-620ml").ValidUntil
            .Should().Be(DateOnly.Parse(validUntil));
    }

    [Fact]
    public void A_tile_with_no_end_date_is_skipped_since_it_would_never_stop_counting()
    {
        var html = Page.Replace("Offer valid until <span class=\"date\">25th November</span>", "Offer valid while stocks last", StringComparison.Ordinal);

        var offers = CentraParser.Parse(html, Today);

        offers.Items.Should().NotContain(i => i.ValidUntil == new DateOnly(2026, 11, 25));
        offers.Unusable.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Offer_addresses_are_normalised_to_a_product_id_and_the_listing_pages_are_not_products()
    {
        Stores.TryNormalizeProductUrl(Store.Centra, "https://www.centra.ie/offers/dairygold-lighter-454g?x=1", out var url).Should().BeTrue();
        url.ToString().Should().Be("https://centra.ie/offers/dairygold-lighter-454g");
        Stores.ProductId(Store.Centra, url.ToString()).Should().Be("dairygold-lighter-454g");
        Stores.ProductId(Store.Centra, "https://centra.ie/page/4821").Should().Be("4821");

        Stores.TryNormalizeProductUrl(Store.Centra, "https://centra.ie/offers", out _).Should().BeFalse();
        Stores.TryNormalizeProductUrl(Store.Centra, "https://centra.ie/offers/all-offers", out _).Should().BeFalse();
        Stores.TryNormalizeProductUrl(Store.Centra, "https://centra.ie/careers", out _).Should().BeFalse();
        Stores.TryNormalizeProductUrl(Store.Centra, "https://example.test/offers/dairygold-lighter-454g", out _).Should().BeFalse();
        Store.Centra.ReadBy().Should().Be("nightly");
        Store.Centra.DisplayName().Should().Be("Centra");
    }

    // ---- the nightly read ----------------------------------------------------------------------------------------

    private readonly FakeTimeProvider _clock = new(Noon);

    private async Task<StorePull> PullAsync(FakeFetcher fetcher, params ListingToCheck[] listings)
    {
        // Each read is a night later than the last, as the job's are.
        _clock.Advance(TimeSpan.FromDays(1));
        var clock = _clock;
        return await _factory.WithDb(async db =>
        {
            var recorder = new CatalogRecorder(db, new PriceRecorder(db));
            var source = new CentraOffersSource(fetcher, recorder, new CatalogIngest(db, recorder, clock), db, clock, NullLogger<CentraOffersSource>.Instance);
            return await source.PullAsync(listings, CancellationToken.None);
        });
    }

    private async Task CleanAsync() =>
        await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            return 0;
        });

    private static FakeFetcher Site() => new FakeFetcher().Serve(CentraOffersSource.OffersUrl, Page);

    [Fact]
    public async Task The_read_records_each_offer_as_a_catalog_price_with_its_end_date_and_marks_it_a_weekly_offer()
    {
        await CleanAsync();

        var pull = await PullAsync(Site());

        pull.Outcome.Should().Be(PullOutcome.Ok);
        var products = await _factory.WithDb(db => db.StoreProducts.Where(p => p.Store == Store.Centra).ToListAsync());
        products.Should().HaveCount(5);
        var beer = products.Single(p => p.ExternalId == "asahi-super-dry-bottle-620ml");
        var price = (await _factory.WithDb(db => db.StoreProductPrices.Where(r => r.StoreProductId == beer.Id).ToListAsync())).Should().ContainSingle().Subject;
        price.Price.Should().Be(3.50m);
        price.ValidUntil.Should().Be(new DateOnly(2026, 11, 25));
        price.PromoText.Should().Be("Weekly offer");
        var run = await _factory.WithDb(db => db.PricePullRuns.SingleAsync(r => r.Store == Store.Centra));
        run.Kind.Should().Be(RunKind.Sweep);
        run.Read.Should().Be(5);
        run.Message.Should().Contain("1 offers");
    }

    [Fact]
    public async Task An_offer_that_leaves_the_page_is_delisted_even_though_half_the_page_turned_over()
    {
        await CleanAsync();
        await PullAsync(Site());

        // Next fortnight: one offer stays, the rest are gone and one is new. A 90 percent guard would call that a half-failed sweep.
        var next = Page.Replace("</body>", "", StringComparison.Ordinal);
        var kept = "asahi-super-dry-bottle-620ml";
        var html = "<h2>Dairy</h2>" + ExtractTile(next, kept) + ExtractTile(next, "santa-rita-120-reserva-especial-merlot-750ml").Replace("santa-rita-120-reserva-especial-merlot-750ml", "new-offer-1l");
        await PullAsync(new FakeFetcher().Serve(CentraOffersSource.OffersUrl, html));

        var live = await _factory.WithDb(db => db.StoreProducts.Where(p => p.Store == Store.Centra && p.DiscontinuedAt == null).Select(p => p.ExternalId).ToListAsync());
        live.Should().BeEquivalentTo(kept, "new-offer-1l");
    }

    private static string ExtractTile(string html, string slug)
    {
        var start = html.LastIndexOf("<a href=\"/offers/" + slug, StringComparison.Ordinal);
        return html[start..(html.IndexOf("</a>", start, StringComparison.Ordinal) + 4)];
    }

    [Fact]
    public async Task An_unreadable_or_empty_page_delists_nothing_and_fails_the_run()
    {
        await CleanAsync();
        await PullAsync(Site());

        var empty = await PullAsync(new FakeFetcher().Serve(CentraOffersSource.OffersUrl, "<html><body>maintenance</body></html>"));
        var gone = await PullAsync(new FakeFetcher());

        empty.Outcome.Should().Be(PullOutcome.Failed);
        gone.Outcome.Should().Be(PullOutcome.Failed);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.Store == Store.Centra && p.DiscontinuedAt == null))).Should().Be(5);
    }

    [Fact]
    public async Task A_refusal_ends_the_read_with_blocked_and_nothing_is_recorded()
    {
        await CleanAsync();
        var fetcher = new FakeFetcher().Serve(CentraOffersSource.OffersUrl, new FetchResult(FetchStatus.Blocked, null, "centra.ie answered 403"));

        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Blocked);
        fetcher.Requested.Should().Equal(CentraOffersSource.OffersUrl);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.Store == Store.Centra))).Should().Be(0);
    }

    [Fact]
    public async Task A_linked_offer_is_priced_from_the_same_read_with_its_end_date_and_a_gone_one_says_so()
    {
        await CleanAsync();
        var linked = new ListingToCheck(Guid.NewGuid(), new Uri("https://centra.ie/offers/asahi-super-dry-bottle-620ml"));
        var gone = new ListingToCheck(Guid.NewGuid(), new Uri("https://centra.ie/offers/last-weeks-offer-1kg"));

        var pull = await PullAsync(Site(), linked, gone);

        var quote = pull.Listings[linked.ListingId].Quote.Should().NotBeNull().And.Subject as PriceQuote;
        quote!.Price.Should().Be(3.50m);
        quote.ValidUntil.Should().Be(new DateOnly(2026, 11, 25));
        quote.PromoText.Should().Be("Weekly offer");
        pull.Listings[gone.ListingId].Error.Should().Contain("no longer list");
    }

    // ---- matching and the plan -----------------------------------------------------------------------------------

    [Fact]
    public async Task Matching_proposes_a_centra_offer_for_a_product_by_the_same_rule_as_the_other_shops()
    {
        await CleanAsync();
        await PullAsync(Site());
        var (owner, _) = await _factory.OwnerAsync();

        var response = await owner.PostAsJsonAsync("/api/products", new { name = "Asahi Super Dry Beer Bottle 620ml" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var proposals = await _factory.WithDb(db => db.MatchCandidates.AsNoTracking()
            .Where(c => c.ProductId == id && c.Store == Store.Centra && c.Status == CandidateStatus.Proposed).ToListAsync());
        proposals.Should().ContainSingle().Which.Url.Should().Be("https://centra.ie/offers/asahi-super-dry-bottle-620ml");
    }

    [Fact]
    public void An_expired_offer_is_not_used_by_the_comparison_and_a_live_one_is_until_its_last_day()
    {
        var offer = new PriceObservation { ObservedOn = Today.AddDays(-3), Price = 1.00m, PromoText = "Weekly offer", ValidUntil = Today.AddDays(-1), InStock = true };
        PriceStates.Of(offer, Today).Should().Be(PriceStates.Ended);

        var lastDay = new PriceObservation { ObservedOn = Today.AddDays(-3), Price = 1.00m, PromoText = "Weekly offer", ValidUntil = Today, InStock = true };
        PriceStates.Of(lastDay, Today).Should().Be(PriceStates.Current);
    }
}
