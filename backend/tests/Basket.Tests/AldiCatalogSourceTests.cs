using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Sources;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basket.Tests;

/// <summary>The Aldi sweep against invented API pages served by a fake fetcher (no network) and a real database.</summary>
public sealed class AldiCatalogSourceTests : IClassFixture<BasketFactory>
{
    private const string Api = "https://asl.api.aldi.ie/commerce";
    private const string TreeUrl = Api + "/v2/product-category-tree?serviceType=walk-in&servicePoint=D105";
    private const string MozzarellaUrl = "https://www.aldi.ie/product/greenfield-grated-mozzarella-000000000000900101";

    private readonly BasketFactory _factory;

    public AldiCatalogSourceTests(BasketFactory factory) => _factory = factory;

    private static string SearchUrl(int offset) => $"{Api}/v3/product-search?currency=EUR&serviceType=walk-in&limit=60&offset={offset}&sort=relevance&servicePoint=D105";

    private static FakeFetcher Range() => new FakeFetcher()
        .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
        .Serve(SearchUrl(0), Fixtures.Read("aldi-product-search.json"));

    /// <summary>A page of <paramref name="count"/> invented products numbered from <paramref name="first"/>, of <paramref name="total"/>.</summary>
    private static string Page(int first, int count, int total)
    {
        var products = string.Join(",", Enumerable.Range(first, count).Select(n =>
            "{\"sku\":\"" + n.ToString("D18") + "\",\"name\":\"Product " + n + "\",\"urlSlugText\":\"product-" + n
            + "\",\"price\":{\"amount\":" + (100 + n) + "},\"categories\":[]}"));
        return "{\"meta\":{\"pagination\":{\"totalCount\":" + total + "}},\"data\":[" + products + "]}";
    }

    private async Task<T> SweepAsync<T>(FakeFetcher fetcher, Func<AldiCatalogSource, Task<T>> run) =>
        await _factory.WithDb(async db =>
        {
            var recorder = new CatalogRecorder(db, new PriceRecorder(db));
            var source = new AldiCatalogSource(
                fetcher,
                recorder,
                new CatalogIngest(db, recorder, _factory.Clock),
                db,
                _factory.Clock,
                NullLogger<AldiCatalogSource>.Instance);
            return await run(source);
        });

    private Task<StorePull> PullAsync(FakeFetcher fetcher, params ListingToCheck[] listings) =>
        SweepAsync(fetcher, s => s.PullAsync(listings, CancellationToken.None));

    private async Task CleanAsync() =>
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            return 0;
        });

    private Task<List<PricePullRun>> SweepRunsAsync() =>
        _factory.WithDb(db => db.PricePullRuns.Where(r => r.Store == Store.Aldi).OrderBy(r => r.FinishedAt).ToListAsync());

    private Task<List<StoreProduct>> ProductsAsync() =>
        _factory.WithDb(db => db.StoreProducts.Where(p => p.Store == Store.Aldi).OrderBy(p => p.ExternalId).ToListAsync());

    [Fact]
    public async Task Aldi_is_read_by_the_nightly_job_and_the_source_sweeps_a_catalog()
    {
        Store.Aldi.ReadBy().Should().Be("nightly");
        Store.Tesco.ReadBy().Should().Be("pc");
        (await SweepAsync(Range(), s => Task.FromResult(s.SweepsCatalog))).Should().BeTrue();
    }

    [Fact]
    public async Task One_tree_request_then_the_range_in_pages_of_sixty_until_the_total()
    {
        await CleanAsync();
        var fetcher = new FakeFetcher()
            .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
            .Serve(SearchUrl(0), Page(1, 60, 125))
            .Serve(SearchUrl(60), Page(61, 60, 125))
            .Serve(SearchUrl(120), Page(121, 5, 125));

        var pull = await PullAsync(fetcher);

        fetcher.Requested.Should().Equal(TreeUrl, SearchUrl(0), SearchUrl(60), SearchUrl(120));
        pull.Outcome.Should().Be(PullOutcome.Ok);
        (await ProductsAsync()).Should().HaveCount(125);
    }

    [Fact]
    public async Task A_product_is_stored_with_its_shop_fields_in_euro_and_its_category_path()
    {
        await CleanAsync();

        var pull = await PullAsync(Range());

        pull.Outcome.Should().Be(PullOutcome.Ok);
        var products = await ProductsAsync();
        products.Select(p => p.ExternalId).Should().Equal("000000000000900101", "000000000000900102", "000000000000900104");

        var mozzarella = products[0];
        mozzarella.Url.Should().Be(MozzarellaUrl);
        mozzarella.Name.Should().Be("Grated Mozzarella");
        mozzarella.Brand.Should().Be("GREENFIELD");
        mozzarella.SizeText.Should().Be("250 g");
        (mozzarella.SizeAmount, mozzarella.SizeUnit).Should().Be((250m, "g"));
        (mozzarella.UnitPrice, mozzarella.UnitPriceBasis).Should().Be((5.96m, "1 KG"));
        mozzarella.ImageUrl.Should().Be("https://images.example.test/product/jpg/scaleWidth/300/mozzarella-0001", "the first picture, at 300 wide");
        mozzarella.Category.Should().Be("Chilled Food > Cheese", "the tree's path of the deepest category");
        mozzarella.DiscontinuedAt.Should().BeNull();

        products[1].Category.Should().Be("Chilled Food > Milk");
        products[2].Category.Should().BeNull("a category the tree does not know is not guessed from the product");
        products[2].ImageUrl.Should().Be("https://images.example.test/special-buy-torch/300/torch");
        products[2].Url.Should().Be("https://www.aldi.ie/product/workzone-special-buy-torch-000000000000900104");
    }

    [Fact]
    public async Task A_product_without_a_slug_from_aldi_is_not_stored()
    {
        await CleanAsync();
        var noSlug = Fixtures.Read("aldi-product-search.json").Replace("\"urlSlugText\": \"workzone-special-buy-torch\",", "", StringComparison.Ordinal);
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), noSlug);

        await PullAsync(fetcher);

        (await ProductsAsync()).Select(p => p.ExternalId).Should().Equal("000000000000900101", "000000000000900102");
    }

    [Fact]
    public async Task Prices_are_euro_from_cents_and_a_was_price_must_be_higher_than_the_price()
    {
        await CleanAsync();

        await PullAsync(Range());

        var rows = await _factory.WithDb(db => db.StoreProductPrices
            .Join(db.StoreProducts, x => x.StoreProductId, p => p.Id, (x, p) => new { p.ExternalId, x.Price, x.WasPrice, x.InStock })
            .OrderBy(x => x.ExternalId).ToListAsync());
        rows.Select(r => (r.ExternalId, r.Price, r.WasPrice, r.InStock)).Should().Equal(
            ("000000000000900101", 1.49m, null, true),
            ("000000000000900102", 2.45m, 2.69m, true),
            ("000000000000900104", 9.99m, null, true));
    }

    [Fact]
    public async Task A_discontinued_product_is_not_recorded_and_a_known_one_is_marked_when_the_sweep_ends()
    {
        await CleanAsync();
        var skus = Enumerable.Range(1, 10).Select(n => $"{n:D18}").ToList();
        var first = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), Page(1, 10, 10));
        await PullAsync(first);
        (await ProductsAsync()).Should().HaveCount(10);

        _factory.Clock.Advance(TimeSpan.FromHours(24));
        var second = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), Page(1, 9, 9));
        var pull = await PullAsync(second);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        pull.Message.Should().Contain("1 products were no longer listed");
        var runs = await SweepRunsAsync();
        runs.Should().HaveCount(2).And.OnlyContain(r => r.Kind == RunKind.Sweep && r.Outcome == PullOutcome.Ok);
        (runs[1].Read, runs[1].Listings).Should().Be((9, 9), "the sweep listed nine and nine are live after the tenth was delisted");
        var products = await ProductsAsync();
        products.Where(p => p.DiscontinuedAt is not null).Select(p => p.ExternalId).Should().Equal(skus[9]);
    }

    [Fact]
    public async Task A_sweep_that_listed_far_less_than_is_known_delists_nothing_and_says_so()
    {
        await CleanAsync();
        var first = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), Page(1, 10, 10));
        await PullAsync(first);

        _factory.Clock.Advance(TimeSpan.FromHours(24));
        var second = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), Page(1, 3, 3));
        var pull = await PullAsync(second);

        pull.Outcome.Should().Be(PullOutcome.Partial);
        pull.Message.Should().Contain("nothing was marked discontinued");
        (await ProductsAsync()).Should().OnlyContain(p => p.DiscontinuedAt == null);
        var last = (await SweepRunsAsync()).Last();
        (last.Kind, last.Outcome, last.Read, last.Listings).Should().Be((RunKind.Sweep, PullOutcome.Partial, 3, 10), "the sweep saw three of ten known and the live catalog is still ten");
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task A_refusal_on_the_tree_ends_the_sweep_at_once_with_no_retry(int status)
    {
        await CleanAsync();
        var fetcher = new FakeFetcher().Serve(TreeUrl, new FetchResult(FetchStatus.Blocked, null, $"asl.api.aldi.ie answered HTTP {status}"));

        var pull = await PullAsync(fetcher, new ListingToCheck(Guid.NewGuid(), new Uri(MozzarellaUrl)));

        pull.Outcome.Should().Be(PullOutcome.Blocked);
        pull.Message.Should().Contain($"HTTP {status}").And.Contain("does not try to get around a block");
        fetcher.Requested.Should().Equal(TreeUrl);
        (await ProductsAsync()).Should().BeEmpty();
        var run = (await SweepRunsAsync()).Single();
        (run.Kind, run.Outcome, run.Read).Should().Be((RunKind.Sweep, PullOutcome.Blocked, 0));
        run.Message.Should().Contain($"HTTP {status}");
    }

    [Fact]
    public async Task A_refusal_part_way_keeps_what_was_read_stops_there_and_delists_nothing()
    {
        await CleanAsync();
        var first = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), Page(1, 10, 10));
        await PullAsync(first);

        _factory.Clock.Advance(TimeSpan.FromHours(24));
        var fetcher = new FakeFetcher()
            .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
            .Serve(SearchUrl(0), Page(1, 60, 200))
            .Serve(SearchUrl(60), new FetchResult(FetchStatus.Blocked, null, "asl.api.aldi.ie answered HTTP 429"))
            .Serve(SearchUrl(120), Page(121, 60, 200));
        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Blocked);
        fetcher.Requested.Should().Equal(TreeUrl, SearchUrl(0), SearchUrl(60));
        var products = await ProductsAsync();
        products.Should().HaveCount(60, "the first page was recorded before the refusal");
        products.Should().OnlyContain(p => p.DiscontinuedAt == null);
        var run = (await SweepRunsAsync()).Last();
        (run.Kind, run.Outcome, run.Read).Should().Be((RunKind.Sweep, PullOutcome.Blocked, 60), "the sweep row says how far it got");
    }

    [Fact]
    public async Task A_category_tree_that_cannot_be_read_costs_only_the_paths()
    {
        await CleanAsync();
        var fetcher = new FakeFetcher()
            .Serve(TreeUrl, new FetchResult(FetchStatus.Failed, null, "asl.api.aldi.ie answered HTTP 500"))
            .Serve(SearchUrl(0), Fixtures.Read("aldi-product-search.json"));

        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        pull.Message.Should().Contain("category tree was not read");
        (await ProductsAsync()).Select(p => p.Category).Should().OnlyContain(c => c == null);
    }

    [Fact]
    public async Task A_page_in_an_unexpected_shape_stops_the_sweep_without_delisting()
    {
        await CleanAsync();
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), """{"hello":"world"}""");

        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Failed);
        pull.Message.Should().Contain("not in the shape");
        (await ProductsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"meta\":{}")]
    [InlineData("\"meta\":{\"pagination\":{\"totalCount\":\"many\"}}")]
    [InlineData("\"paging\":{\"pagination\":{\"totalCount\":900}}")]
    public async Task A_page_without_a_readable_total_is_not_a_finished_sweep(string meta)
    {
        await CleanAsync();
        var products = Page(1, 3, 3);
        var body = "{" + meta + products[products.IndexOf(",\"data\"", StringComparison.Ordinal)..];
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), body);

        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Failed);
        pull.Message.Should().Contain("not in the shape");
        (await ProductsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_where_no_product_has_a_price_is_a_changed_shape_and_wipes_nothing()
    {
        await CleanAsync();
        var first = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), Page(1, 10, 10));
        await PullAsync(first);

        _factory.Clock.Advance(TimeSpan.FromHours(24));
        var renamed = Page(1, 10, 10).Replace("\"amount\"", "\"cost\"", StringComparison.Ordinal);
        var pull = await PullAsync(new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), renamed));

        pull.Outcome.Should().Be(PullOutcome.Failed);
        pull.Message.Should().Contain("not in the shape").And.Contain("no product had a price");
        var prices = await _factory.WithDb(db => db.StoreProductPrices.Select(x => x.Price).ToListAsync());
        prices.Should().HaveCount(10).And.OnlyContain(p => p != null, "no empty price row was written over the known prices");
        (await ProductsAsync()).Should().OnlyContain(p => p.DiscontinuedAt == null);
    }

    [Fact]
    public async Task An_unpriced_trailing_product_does_not_stop_a_sweep_that_priced_the_rest()
    {
        await CleanAsync();
        var unpriced = Page(61, 1, 61).Replace("\"amount\"", "\"cost\"", StringComparison.Ordinal);
        var fetcher = new FakeFetcher()
            .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
            .Serve(SearchUrl(0), Page(1, 60, 61))
            .Serve(SearchUrl(60), unpriced);

        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        (await ProductsAsync()).Should().HaveCount(61);
    }

    [Fact]
    public async Task The_request_limit_cuts_a_sweep_short_and_nothing_is_delisted()
    {
        await CleanAsync();
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"));
        for (var page = 0; page < 70; page++)
        {
            fetcher.Serve(SearchUrl(page * 60), Page((page * 60) + 1, 60, 6000));
        }

        var pull = await PullAsync(fetcher);

        fetcher.Requested.Should().HaveCount(61, "one tree request and 60 pages").And.StartWith(new[] { TreeUrl, SearchUrl(0), SearchUrl(60) });
        pull.Outcome.Should().Be(PullOutcome.Partial);
        pull.Message.Should().Contain("request limit");
    }

    /// <summary>The invented range of 3,708 products: 62 pages, two more than a night's budget.</summary>
    private static FakeFetcher LongRange()
    {
        const int total = 3708;
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"));
        for (var offset = 0; offset < total; offset += 60)
        {
            fetcher.Serve(SearchUrl(offset), Page(offset + 1, Math.Min(60, total - offset), total));
        }

        return fetcher;
    }

    [Fact]
    public async Task A_range_longer_than_the_page_budget_is_read_from_where_the_last_sweep_stopped_and_wraps()
    {
        await CleanAsync();

        var first = LongRange();
        var night1 = await PullAsync(first);
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        var second = LongRange();
        var night2 = await PullAsync(second);
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        var third = LongRange();
        await PullAsync(third);

        // Night 1 reads offsets 0..3540 and stops; night 2 begins at 3600, wraps past the last page to 0 and stops at 3420.
        first.Requested.Should().HaveCount(61).And.Contain(SearchUrl(3540)).And.NotContain(SearchUrl(3600));
        night1.Outcome.Should().Be(PullOutcome.Partial);
        second.Requested.Skip(1).Take(3).Should().Equal(SearchUrl(3600), SearchUrl(3660), SearchUrl(0));
        second.Requested.Should().HaveCount(61).And.Contain(SearchUrl(3420)).And.NotContain(SearchUrl(3480));
        night2.Outcome.Should().Be(PullOutcome.Partial);
        third.Requested[1].Should().Be(SearchUrl(3480), "night 3 carries on where night 2 stopped");

        (await SweepRunsAsync()).Select(r => r.NextOffset).Should().Equal(3600, 3480, 3360);
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_at_the_start_is_stepped_over_so_it_does_not_hold_the_range_back()
    {
        await CleanAsync();

        // 200 products; the page at offset 60 never answers.
        FakeFetcher Range200() => new FakeFetcher()
            .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
            .Serve(SearchUrl(0), Page(1, 60, 200))
            .Serve(SearchUrl(120), Page(121, 60, 200))
            .Serve(SearchUrl(180), Page(181, 20, 200));

        var night1 = Range200();
        await PullAsync(night1);
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        var night2 = Range200();
        var pull2 = await PullAsync(night2);
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        var night3 = Range200();
        var pull3 = await PullAsync(night3);

        night1.Requested.Should().Equal(TreeUrl, SearchUrl(0), SearchUrl(60));
        night2.Requested.Should().Equal(TreeUrl, SearchUrl(60));
        pull2.Outcome.Should().Be(PullOutcome.Failed);
        night3.Requested.Should().StartWith(new[] { TreeUrl, SearchUrl(120), SearchUrl(180) }, "night 3 starts past the bad page");
        pull3.Outcome.Should().Be(PullOutcome.Partial);
        (await SweepRunsAsync()).Select(r => r.NextOffset).Should().Equal(60, 120, 60);
    }

    [Fact]
    public async Task A_start_beyond_a_range_that_shrank_is_read_from_the_top()
    {
        await CleanAsync();

        await PullAsync(LongRange());
        (await SweepRunsAsync()).Single().NextOffset.Should().Be(3600);
        _factory.Clock.Advance(TimeSpan.FromDays(1));

        var shrunk = new FakeFetcher()
            .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
            .Serve(SearchUrl(0), Page(1, 60, 200))
            .Serve(SearchUrl(3600), "{\"meta\":{\"pagination\":{\"totalCount\":200}},\"data\":[]}");
        var pull = await PullAsync(shrunk);

        shrunk.Requested.Should().StartWith(new[] { TreeUrl, SearchUrl(3600), SearchUrl(0) });
        pull.Outcome.Should().Be(PullOutcome.Partial);
    }

    [Fact]
    public async Task A_start_beyond_a_range_that_shrank_and_does_not_answer_wraps_to_the_top_instead_of_stepping_on()
    {
        await CleanAsync();

        await PullAsync(LongRange());
        _factory.Clock.Advance(TimeSpan.FromDays(1));

        var shrunk = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"));
        var pull = await PullAsync(shrunk);

        shrunk.Requested.Should().Equal(TreeUrl, SearchUrl(3600));
        pull.Outcome.Should().Be(PullOutcome.Failed);
        (await SweepRunsAsync()).Select(r => r.NextOffset).Should().Equal(3600, 0);
    }

    [Fact]
    public async Task The_whole_range_of_about_3554_products_is_sixty_pages_and_closes_within_the_limit()
    {
        await CleanAsync();
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"));
        for (var page = 0; page < 60; page++)
        {
            fetcher.Serve(SearchUrl(page * 60), Page((page * 60) + 1, Math.Min(60, 3554 - (page * 60)), 3554));
        }

        var pull = await PullAsync(fetcher);

        fetcher.Requested.Should().HaveCount(61);
        pull.Outcome.Should().Be(PullOutcome.Ok);
        (await ProductsAsync()).Should().HaveCount(3554);
    }

    [Fact]
    public async Task A_category_tree_in_any_other_shape_gives_no_categories_and_the_sweep_goes_on()
    {
        await CleanAsync();
        var bare = """[{"key":"1110000000","name":"Cheese","children":[]}]""";
        var fetcher = new FakeFetcher().Serve(TreeUrl, bare).Serve(SearchUrl(0), Fixtures.Read("aldi-product-search.json"));

        var pull = await PullAsync(fetcher);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        (await ProductsAsync()).Should().OnlyContain(p => p.Category == null);
    }

    [Fact]
    public async Task A_linked_product_is_priced_from_the_sweep_without_opening_its_own_page()
    {
        await CleanAsync();
        var fetcher = Range();
        var mozzarella = new ListingToCheck(Guid.NewGuid(), new Uri(MozzarellaUrl));
        var milk = new ListingToCheck(Guid.NewGuid(), new Uri("https://www.aldi.ie/product/some-old-slug-000000000000900102"));
        var gone = new ListingToCheck(Guid.NewGuid(), new Uri("https://www.aldi.ie/product/never-listed-000000000000999999"));
        var discontinued = new ListingToCheck(Guid.NewGuid(), new Uri("https://www.aldi.ie/product/oakridge-old-style-water-000000000000900103"));

        var pull = await PullAsync(fetcher, mozzarella, milk, gone, discontinued);

        fetcher.Requested.Should().Equal(TreeUrl, SearchUrl(0));
        pull.Outcome.Should().Be(PullOutcome.Partial);
        var quote = pull.Listings[mozzarella.ListingId].Quote!;
        (quote.Price, quote.WasPrice, quote.InStock, quote.Category).Should().Be((1.49m, null, true, "Chilled Food > Cheese"));
        quote.ImageUrl.Should().Be("https://images.example.test/product/jpg/scaleWidth/300/mozzarella-0001");
        pull.Listings[milk.ListingId].Quote!.WasPrice.Should().Be(2.69m, "the product is found by its number, the slug in the stored address may be old");
        pull.Listings[gone.ListingId].Error.Should().Contain("no longer lists");
        pull.Listings[discontinued.ListingId].Error.Should().Contain("discontinued");
    }

    [Fact]
    public async Task A_linked_product_listed_without_a_price_fails_and_keeps_its_last_price()
    {
        await CleanAsync();
        var torch = new ListingToCheck(Guid.NewGuid(), new Uri("https://www.aldi.ie/product/special-buy-torch-000000000000900104"));
        var priced = Fixtures.Read("aldi-product-search.json");
        var unpricedTorch = priced.Replace("\"price\": { \"amount\": 999, \"wasPriceDisplay\": \"€9.99\" }", "\"price\": {}", StringComparison.Ordinal);
        unpricedTorch.Should().NotBe(priced, "the fixture's torch must lose its price for this test");
        var fetcher = new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), unpricedTorch);

        var pull = await PullAsync(fetcher, torch);

        pull.Listings[torch.ListingId].Quote.Should().BeNull("a missing price is not a price of nothing");
        pull.Listings[torch.ListingId].Error.Should().Contain("without a price");
    }

    [Fact]
    public async Task A_listing_whose_address_carries_no_number_is_not_priced()
    {
        await CleanAsync();

        var odd = new ListingToCheck(Guid.NewGuid(), new Uri("https://www.aldi.ie/results?q=milk"));
        var pull = await PullAsync(Range(), odd);

        pull.Listings[odd.ListingId].Quote.Should().BeNull();
        pull.Outcome.Should().Be(PullOutcome.Failed);
    }

    [Fact]
    public async Task A_product_seen_again_the_next_night_keeps_one_row_and_a_new_price_is_a_new_history_row()
    {
        await CleanAsync();
        await PullAsync(Range());

        _factory.Clock.Advance(TimeSpan.FromHours(24));
        var cheaper = Fixtures.Read("aldi-product-search.json").Replace("\"amount\": 149", "\"amount\": 129", StringComparison.Ordinal);
        await PullAsync(new FakeFetcher().Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json")).Serve(SearchUrl(0), cheaper));

        (await ProductsAsync()).Should().HaveCount(3);
        var mozzarellaPrices = await _factory.WithDb(db => db.StoreProductPrices
            .Where(x => db.StoreProducts.Any(p => p.Id == x.StoreProductId && p.ExternalId == "000000000000900101"))
            .OrderBy(x => x.ObservedOn).Select(x => x.Price).ToListAsync());
        mozzarellaPrices.Should().Equal(1.49m, 1.29m);
    }
}
