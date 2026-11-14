using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Fetching;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Basket.Tests;

/// <summary>The nightly Lidl catalog sweep against a real database and recorded (synthetic) shop pages: no network.</summary>
public sealed class LidlCatalogSweepTests : IClassFixture<BasketFactory>
{
    private const string Root = "https://www.lidl.ie/c/food-drink/s90000100";
    private const string FruitVeg = "https://www.lidl.ie/c/fresh-fruit-and-vegetables/s90000101";
    private const string Chilled = "https://www.lidl.ie/c/chilled-meat-and-fish/s90000105";
    private const string Index = "https://www.lidl.ie/static/sitemap.xml";
    private const string PagesSitemap = "https://www.lidl.ie/static/pages_en-IE_ie.xml.gz";
    private const string ButterPage = "https://www.lidl.ie/p/dairyfield-unsalted-butter/p90000001";
    private const string BiscuitsPage = "https://www.lidl.ie/p/crumbly-oat-biscuits/p90000002";
    private const string TeaPage = "https://www.lidl.ie/p/teahouse-tea-bags/p90000003";
    private const string LentilsPage = "https://www.lidl.ie/p/red-lentils/p90000004";

    private readonly BasketFactory _factory;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-14T03:30:00Z"));

    public LidlCatalogSweepTests(BasketFactory factory) => _factory = factory;

    private static FakeFetcher Shop(bool withBarcodes = true)
    {
        var fetcher = new FakeFetcher()
            .Serve(Index, Fixtures.Read("lidl-sitemap-index.xml"))
            .Serve(PagesSitemap, Fixtures.Read("lidl-pages-sitemap.xml"))
            .Serve(Root, Fixtures.Read("lidl-catalog-page-1.html"))
            .Serve(Root + "?offset=12", Fixtures.Read("lidl-catalog-page-2.html"))
            .Serve(Root + "?offset=24", Fixtures.Read("lidl-catalog-page-empty.html"))
            .Serve(FruitVeg, Fixtures.Read("lidl-catalog-page-2.html"))
            .Serve(FruitVeg + "?offset=12", Fixtures.Read("lidl-catalog-page-empty.html"))
            .Serve(Chilled, Fixtures.Read("lidl-catalog-page-2.html"));
        if (withBarcodes)
        {
            fetcher.Serve(ButterPage, Fixtures.Read("lidl-product-page-gtin.html"))
                .Serve(BiscuitsPage, Fixtures.Read("lidl-product-page-bad-gtin.html"))
                .Serve(TeaPage, Fixtures.Read("lidl-product-page-gtin.html"))
                .Serve(LentilsPage, Fixtures.Read("lidl-product-page-gtin.html"));
        }

        return fetcher;
    }

    private Task SweepAsync(FakeFetcher fetcher, int barcodeCap = 200) =>
        _factory.WithDb(async db =>
        {
            var recorder = new CatalogRecorder(db);
            var options = Options.Create(new PricesOptions
            {
                LidlBarcodePagesPerNight = barcodeCap,
            });
            var sweep = new LidlCatalogSweep(
                fetcher, db, recorder, new CatalogIngest(db, recorder, _clock), options, _clock, NullLogger<LidlCatalogSweep>.Instance);
            await sweep.RunAsync(CancellationToken.None);
            return 0;
        });

    private Task CleanAsync() =>
        _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            return 0;
        });

    private Task<List<StoreProduct>> ProductsAsync() =>
        _factory.WithDb(db => db.StoreProducts.Where(p => p.Store == Store.Lidl).OrderBy(p => p.ExternalId).ToListAsync());

    [Fact]
    public async Task The_sweep_stores_every_listed_product_and_a_product_without_a_price_is_not_an_error()
    {
        await CleanAsync();

        await SweepAsync(Shop());

        var products = await ProductsAsync();
        products.Select(p => p.ExternalId).Should().Equal("90000001", "90000002", "90000003", "90000004");
        var butter = products[0];
        butter.Name.Should().Be("DAIRYFIELD Unsalted Butter 250g");
        butter.Brand.Should().Be("DAIRYFIELD");
        butter.Category.Should().Be("Food and near food > Dairy > Butter");
        butter.SizeAmount.Should().Be(250);
        butter.SizeUnit.Should().Be("g");
        butter.DiscontinuedAt.Should().BeNull();

        await _factory.WithDb(async db =>
        {
            var prices = await db.StoreProductPrices.Join(db.StoreProducts, x => x.StoreProductId, p => p.Id, (x, p) => new { p.ExternalId, x }).ToListAsync();
            prices.Select(p => p.ExternalId).Should().BeEquivalentTo(["90000001", "90000002", "90000004"], "the tea has no price and gets no row");
            prices.Single(p => p.ExternalId == "90000001").x.Price.Should().Be(2.49m);
            prices.Single(p => p.ExternalId == "90000002").x.LoyaltyPrice.Should().Be(1.19m);
            return 0;
        });
    }

    [Fact]
    public async Task Listing_pages_are_walked_by_offset_until_nothing_new_and_the_sitemap_adds_the_food_categories()
    {
        await CleanAsync();
        var fetcher = Shop();

        await SweepAsync(fetcher);

        fetcher.Requested.Should().Equal(
            Index, PagesSitemap,
            Root, Root + "?offset=12", Root + "?offset=24",
            FruitVeg, // nothing it lists is new, so its second page is not asked for
            Chilled,
            ButterPage, BiscuitsPage, TeaPage, LentilsPage);
        fetcher.Requested.Should().NotContain(u => u.Contains("/q/api/search"));
        fetcher.Requested.Should().NotContain(u => u.Contains("garden") || u.Contains("power-tools") || u.Contains("example.com"));
    }

    [Fact]
    public async Task The_sweep_closes_with_a_run_row_through_the_shared_completion()
    {
        await CleanAsync();

        await SweepAsync(Shop());

        await _factory.WithDb(async db =>
        {
            var run = await db.PricePullRuns.SingleAsync(r => r.Store == Store.Lidl);
            run.Outcome.Should().Be(PullOutcome.Ok);
            run.Read.Should().Be(4);
            run.Listings.Should().Be(4);
            return 0;
        });
    }

    [Fact]
    public async Task Only_products_without_a_barcode_have_their_page_opened_and_a_valid_gtin_is_stored()
    {
        await CleanAsync();
        await SweepAsync(Shop());
        var products = await ProductsAsync();
        products.Select(p => p.Barcode).Should().Equal("04000000000013", null, "04000000000013", "04000000000013");

        _clock.Advance(TimeSpan.FromDays(1));
        var second = Shop();
        await SweepAsync(second);

        second.Requested.Should().Contain(BiscuitsPage, "its page had no valid barcode, so it is tried again");
        second.Requested.Should().NotContain([ButterPage, TeaPage, LentilsPage], "they already have a barcode");
    }

    [Fact]
    public async Task At_most_the_nightly_cap_of_product_pages_is_opened()
    {
        await CleanAsync();
        var fetcher = Shop();

        await SweepAsync(fetcher, barcodeCap: 2);

        fetcher.Requested.Count(u => u.Contains("/p/")).Should().Be(2);
        (await ProductsAsync()).Count(p => p.Barcode != null).Should().BeInRange(1, 2);
    }

    [Fact]
    public async Task A_page_that_never_gives_a_barcode_cannot_hold_the_cap_every_night()
    {
        await CleanAsync();

        for (var night = 0; night < 6; night++)
        {
            await SweepAsync(Shop(), barcodeCap: 1);
            _clock.Advance(TimeSpan.FromDays(1));
        }

        (await ProductsAsync()).Where(p => p.ExternalId != "90000002").Should().OnlyContain(p => p.Barcode != null, "the bad page took one night in a few, not every night");
    }

    [Fact]
    public async Task A_repeat_sweep_writes_no_new_price_rows_and_keeps_the_barcodes()
    {
        await CleanAsync();
        await SweepAsync(Shop());
        var before = await _factory.WithDb(db => db.StoreProductPrices.CountAsync());

        _clock.Advance(TimeSpan.FromDays(1));
        await SweepAsync(Shop(withBarcodes: false));

        (await _factory.WithDb(db => db.StoreProductPrices.CountAsync())).Should().Be(before);
        (await ProductsAsync()).Select(p => p.Barcode).Should().Equal("04000000000013", null, "04000000000013", "04000000000013");
    }

    [Fact]
    public async Task A_refusal_ends_the_sweep_marks_nothing_discontinued_and_is_recorded()
    {
        await CleanAsync();
        await SweepAsync(Shop());
        _clock.Advance(TimeSpan.FromDays(1));
        var refused = Shop().Serve(Root + "?offset=12", new FetchResult(FetchStatus.Blocked, null, "www.lidl.ie answered HTTP 429"));

        await SweepAsync(refused);

        refused.Requested.Should().NotContain(FruitVeg, "nothing is asked for after a refusal");
        refused.Requested.Where(u => u.Contains("/p/")).Should().BeEmpty();
        await _factory.WithDb(async db =>
        {
            var run = await db.PricePullRuns.Where(r => r.Store == Store.Lidl).OrderByDescending(r => r.StartedAt).FirstAsync();
            run.Outcome.Should().Be(PullOutcome.Blocked);
            run.Message.Should().Contain("429").And.Contain("does not try to get around");
            (await db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null)).Should().Be(0);
            return 0;
        });
    }

    [Fact]
    public async Task A_refused_sitemap_ends_the_sweep_before_any_listing_page()
    {
        await CleanAsync();
        var fetcher = Shop().Serve(Index, new FetchResult(FetchStatus.Blocked, null, "www.lidl.ie answered HTTP 403"));

        await SweepAsync(fetcher);

        fetcher.Requested.Should().Equal(Index);
        (await ProductsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_sitemap_walks_nothing_and_the_run_says_failed()
    {
        await CleanAsync();
        var fetcher = Shop().Serve(Index, new FetchResult(FetchStatus.Failed, null, "down"));

        await SweepAsync(fetcher);

        fetcher.Requested.Should().Equal(Index);
        await _factory.WithDb(async db =>
        {
            var run = await db.PricePullRuns.SingleAsync(r => r.Store == Store.Lidl);
            run.Outcome.Should().Be(PullOutcome.Failed);
            run.Message.Should().Contain("sitemap index was not read");
            return 0;
        });
    }

    private async Task AssertIncompleteSweepDelistsNothingAsync(FakeFetcher incomplete, string reason)
    {
        await CleanAsync();
        await SweepAsync(Shop());
        _clock.Advance(TimeSpan.FromDays(1));

        await SweepAsync(incomplete);

        await _factory.WithDb(async db =>
        {
            (await db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null)).Should().Be(0, "a sweep that missed a page must not delist, however small the gap");
            var run = await db.PricePullRuns.Where(r => r.Store == Store.Lidl).OrderByDescending(r => r.StartedAt).FirstAsync();
            run.Outcome.Should().Be(PullOutcome.Partial);
            run.Message.Should().Contain(reason).And.Contain("Nothing was marked discontinued");
            return 0;
        });
    }

    [Fact]
    public Task A_listing_page_that_is_down_delists_nothing() =>
        AssertIncompleteSweepDelistsNothingAsync(
            Shop().Serve(Root + "?offset=12", new FetchResult(FetchStatus.Failed, null, "HTTP 500")), "was not read");

    [Fact]
    public Task A_first_page_with_no_products_delists_nothing() =>
        AssertIncompleteSweepDelistsNothingAsync(
            Shop().Serve(Chilled, Fixtures.Read("lidl-catalog-page-empty.html")), "showed no products");

    [Fact]
    public Task A_page_of_tiles_that_are_all_unusable_delists_nothing() =>
        AssertIncompleteSweepDelistsNothingAsync(
            Shop().Serve(
                Chilled,
                Fixtures.Read("lidl-catalog-page-2.html").Replace("erpNumber&quot;: &quot;90000003&quot;", "erpNumber&quot;: &quot;&quot;")
                    .Replace("erpNumber&quot;: &quot;90000004&quot;", "erpNumber&quot;: &quot;&quot;")),
            "no usable products");

    [Fact]
    public async Task A_category_that_never_ends_is_cut_at_the_ceiling_and_delists_nothing()
    {
        await CleanAsync();
        await SweepAsync(Shop());
        _clock.Advance(TimeSpan.FromDays(1));
        var endless = Shop();
        for (var page = 2; page < 130; page++)
        {
            var listing = Fixtures.Read("lidl-catalog-page-2.html")
                .Replace("90000003", (91000000 + page * 2).ToString()).Replace("90000004", (91000001 + page * 2).ToString());
            endless.Serve($"{Root}?offset={page * LidlCatalogSweep.PageSize}", listing);
        }

        await SweepAsync(endless);

        endless.Requested.Count(u => u.StartsWith(Root)).Should().BeLessThanOrEqualTo(120);
        (await _factory.WithDb(db => db.StoreProducts.CountAsync(p => p.DiscontinuedAt != null))).Should().Be(0);
    }
}
