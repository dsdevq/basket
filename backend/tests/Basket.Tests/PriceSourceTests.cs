using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Sources;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Basket.Tests;

/// <summary>Each shop's source against recorded pages served by a fake fetcher.</summary>
public sealed class PriceSourceTests
{
    private const string OffersPage = "https://www.lidl.ie/c/store/lidl-plus-offers/a10073407";
    private const string SkewersUrl = "https://www.lidl.ie/p/vitasia-10-chicken-breast-skewers/p11115738";
    private const string AubergineUrl = "https://www.lidl.ie/p/aubergine/p11214436";
    private static readonly string Denied = Fixtures.Read("akamai-access-denied.html");

    private static ListingToCheck Listing(string url) => new(Guid.NewGuid(), new Uri(url));

    private static LidlPriceSource Lidl(FakeFetcher fetcher, params string[] offerPages) =>
        new(fetcher, Options.Create(new PricesOptions { LidlOfferPages = offerPages }), NullLogger<LidlPriceSource>.Instance);

    [Fact]
    public async Task Lidl_takes_a_listed_product_from_the_offer_page_without_asking_for_its_own_page()
    {
        var fetcher = new FakeFetcher().Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"));
        var skewers = Listing(SkewersUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        var quote = pull.Listings[skewers.ListingId].Quote!;
        quote.Price.Should().Be(2.19m);
        quote.LoyaltyPrice.Should().Be(2.19m);
        quote.ImageUrl.Should().Be("https://www.lidl.ie/assets/gcpa9b8e188dc8a48039b8b578c2c9466bf.png");
        fetcher.Requested.Should().Equal(OffersPage);
    }

    [Fact]
    public async Task Lidl_opens_a_listed_products_own_page_once_for_its_category_and_keeps_the_offer_price()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"))
            .Serve(SkewersUrl, Fixtures.Read("lidl-product-page-breadcrumb.html"));
        var skewers = Listing(SkewersUrl) with { NeedsCategory = true };

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers], CancellationToken.None);

        var quote = pull.Listings[skewers.ListingId].Quote!;
        quote.Price.Should().Be(2.19m);
        quote.InStock.Should().BeTrue("the offer tile says so, the product page's stock is not the offer's");
        quote.Category.Should().Be("Food & Drink > Meat & Poultry");
        fetcher.Requested.Should().Equal(OffersPage, SkewersUrl);
    }

    [Fact]
    public async Task Lidl_loses_only_the_category_when_a_listed_products_own_page_is_down()
    {
        var fetcher = new FakeFetcher().Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"));
        var skewers = Listing(SkewersUrl) with { NeedsCategory = true };

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        pull.Listings[skewers.ListingId].Quote!.Price.Should().Be(2.19m);
        pull.Listings[skewers.ListingId].Quote!.Category.Should().BeNull();
    }

    [Fact]
    public async Task Lidl_stops_the_pull_when_a_listed_products_own_page_is_refused_for_its_category()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"))
            .Serve(SkewersUrl, new FetchResult(FetchStatus.Blocked, null, "www.lidl.ie answered HTTP 403"));
        var skewers = Listing(SkewersUrl) with { NeedsCategory = true };
        var other = Listing(AubergineUrl) with { NeedsCategory = true };

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers, other], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Blocked);
        pull.Message.Should().Contain("403");
        fetcher.Requested.Should().Equal(OffersPage, SkewersUrl);
    }

    [Fact]
    public async Task Lidl_reads_the_product_page_of_anything_the_offer_pages_do_not_list()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"))
            .Serve(AubergineUrl, Fixtures.Read("lidl-product-page-no-price.html"));
        var skewers = Listing(SkewersUrl);
        var aubergine = Listing(AubergineUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers, aubergine], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Ok);
        pull.Listings[aubergine.ListingId].Quote!.Price.Should().BeNull("Lidl lists no price this week, which is itself the reading");
        pull.Listings[aubergine.ListingId].Quote!.Title.Should().Be("Aubergine");
        pull.Listings[aubergine.ListingId].Quote!.ImageUrl.Should().Be("https://www.lidl.ie/assets/gcpbc4f96d4506e494eabf4ba0c20bb0335.png");
        fetcher.Requested.Should().Equal(OffersPage, AubergineUrl);
    }

    [Fact]
    public async Task Lidl_keeps_going_when_an_offer_page_is_down_and_one_product_is_gone()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, new FetchResult(FetchStatus.Failed, null, "timeout"))
            .Serve(SkewersUrl, Fixtures.Read("lidl-product-page.html"));
        var skewers = Listing(SkewersUrl);
        var gone = Listing("https://www.lidl.ie/p/gone/p11999999");

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers, gone], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Partial);
        pull.Listings[skewers.ListingId].Quote!.Price.Should().Be(2.19m);
        pull.Listings[gone.ListingId].Error.Should().Contain("404");
    }

    [Fact]
    public async Task Lidl_does_not_record_no_price_for_a_product_when_an_offer_page_was_down()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, new FetchResult(FetchStatus.Failed, null, "timeout"))
            .Serve(AubergineUrl, Fixtures.Read("lidl-product-page-no-price.html"));
        var aubergine = Listing(AubergineUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([aubergine], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Failed);
        pull.Listings[aubergine.ListingId].Quote.Should().BeNull();
        pull.Listings[aubergine.ListingId].Error.Should().Contain("timeout");
    }

    [Fact]
    public async Task Lidl_does_not_record_no_price_for_a_product_when_an_offer_page_came_back_without_products()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, "<html>redesigned</html>")
            .Serve(AubergineUrl, Fixtures.Read("lidl-product-page-no-price.html"));
        var aubergine = Listing(AubergineUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([aubergine], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Failed);
        pull.Listings[aubergine.ListingId].Quote.Should().BeNull();
        pull.Listings[aubergine.ListingId].Error.Should().Contain("page layout");
    }

    [Fact]
    public async Task Lidl_stops_at_the_first_refusal_and_asks_for_nothing_more()
    {
        var fetcher = new FakeFetcher().Serve(OffersPage, new FetchResult(FetchStatus.Blocked, null, "www.lidl.ie answered HTTP 403"));

        var pull = await Lidl(fetcher, OffersPage, "https://www.lidl.ie/c/second").PullAsync([Listing(SkewersUrl)], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Blocked);
        pull.Message.Should().Contain("403").And.Contain("does not try to get around");
        fetcher.Requested.Should().Equal(OffersPage);
    }

    [Fact]
    public async Task A_lidl_page_that_changed_shape_is_a_failure_for_that_product_only()
    {
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"))
            .Serve(AubergineUrl, "<html><body>redesigned</body></html>");
        var skewers = Listing(SkewersUrl);
        var aubergine = Listing(AubergineUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers, aubergine], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Partial);
        pull.Listings[skewers.ListingId].Quote.Should().NotBeNull();
        pull.Listings[aubergine.ListingId].Error.Should().Contain("page layout");
    }

    [Fact]
    public void The_recorded_denial_page_is_what_the_fetcher_treats_as_a_block()
    {
        PoliteFetcher.LooksLikeBlockPage(Denied).Should().BeTrue();
        PoliteFetcher.LooksLikeBlockPage(Fixtures.Read("lidl-product-page.html")).Should().BeFalse();
    }
}
