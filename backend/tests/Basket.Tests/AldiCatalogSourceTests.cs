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
    private const string Api = "https://asl.api.aldi.ie/v3";
    private const string TreeUrl = Api + "/product-category-tree?currency=EUR";
    private const string MozzarellaUrl = "https://www.aldi.ie/product/greenfield-grated-mozzarella-000000000000900101";

    private readonly BasketFactory _factory;

    public AldiCatalogSourceTests(BasketFactory factory) => _factory = factory;

    private static string SearchUrl(int offset) => $"{Api}/product-search?currency=EUR&limit=60&offset={offset}";

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

    private async Task<T> SweepAsync<T>(FakeFetcher fetcher, Func<AldiCatalogSource, Task<T>> run, int maxRequests = 60) =>
        await _factory.WithDb(async db =>
        {
            var recorder = new CatalogRecorder(db);
            var source = new AldiCatalogSource(
                fetcher,
                recorder,
                new CatalogIngest(db, recorder, _factory.Clock),
                db,
                _factory.Clock,
                NullLogger<AldiCatalogSource>.Instance) { MaxRequests = maxRequests };
            return await run(source);
        });

    private Task<StorePull> PullAsync(FakeFetcher fetcher, params ListingToCheck[] listings) =>
        SweepAsync(fetcher, s => s.PullAsync(listings, CancellationToken.None));

    private async Task CleanAsync() =>
        await _factory.WithDb(async db =>
        {
            await db.StoreProducts.ExecuteDeleteAsync();
            return 0;
        });

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
        (mozzarella.UnitPrice, mozzarella.UnitPriceBasis).Should().Be((5.96m, "1 kg"));
        mozzarella.ImageUrl.Should().Be("https://images.example.test/product/jpg/scaleWidth/300/mozzarella-0001", "the first picture, at 300 wide");
        mozzarella.Category.Should().Be("Chilled Food > Cheese", "the tree's path, without the catalogue root");
        mozzarella.DiscontinuedAt.Should().BeNull();

        products[1].Category.Should().Be("Chilled Food > Milk");
        products[2].Category.Should().Be("Special Buys", "a category the tree does not know is filed under the name the product carries");
        products[2].ImageUrl.Should().Be("https://images.example.test/special-buy-torch/300/torch");
        products[2].Url.Should().Be("https://www.aldi.ie/product/special-buy-torch-000000000000900104", "no slug from Aldi, so one is made from the name");
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
        (await ProductsAsync()).Select(p => p.Category).Should().Equal("Cheese", "Milk", "Special Buys");
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
        var fetcher = new FakeFetcher()
            .Serve(TreeUrl, Fixtures.Read("aldi-category-tree.json"))
            .Serve(SearchUrl(0), Page(1, 60, 600))
            .Serve(SearchUrl(60), Page(61, 60, 600));

        var pull = await SweepAsync(fetcher, s => s.PullAsync([], CancellationToken.None), maxRequests: 3);

        fetcher.Requested.Should().Equal(TreeUrl, SearchUrl(0), SearchUrl(60));
        pull.Outcome.Should().Be(PullOutcome.Partial);
        pull.Message.Should().Contain("request limit");
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
