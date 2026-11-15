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
    public async Task Lidl_leaves_a_product_the_offer_pages_do_not_list_alone_and_never_opens_its_page()
    {
        var fetcher = new FakeFetcher().Serve(OffersPage, Fixtures.Read("lidl-offers-page.html"));
        var skewers = Listing(SkewersUrl);
        var aubergine = Listing(AubergineUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers, aubergine], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Ok, "a product the catalog sweep prices is not a failure of the offer read");
        pull.Listings[skewers.ListingId].Quote!.Price.Should().Be(2.19m);
        pull.Listings[aubergine.ListingId].IsUnlisted.Should().BeTrue();
        fetcher.Requested.Should().Equal(OffersPage);
    }

    [Fact]
    public async Task Lidl_reports_a_down_offer_page_on_the_run_and_still_prices_what_the_others_list()
    {
        var second = "https://www.lidl.ie/c/second";
        var fetcher = new FakeFetcher()
            .Serve(OffersPage, new FetchResult(FetchStatus.Failed, null, "timeout"))
            .Serve(second, Fixtures.Read("lidl-offers-page.html"));
        var skewers = Listing(SkewersUrl);
        var aubergine = Listing(AubergineUrl);

        var pull = await Lidl(fetcher, OffersPage, second).PullAsync([skewers, aubergine], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Partial);
        pull.Message.Should().Contain("timeout");
        pull.Listings[skewers.ListingId].Quote!.Price.Should().Be(2.19m);
        pull.Listings[aubergine.ListingId].IsUnlisted.Should().BeTrue();
    }

    [Fact]
    public async Task Lidl_fails_the_run_when_no_offer_page_had_products()
    {
        var fetcher = new FakeFetcher().Serve(OffersPage, "<html>redesigned</html>");
        var skewers = Listing(SkewersUrl);

        var pull = await Lidl(fetcher, OffersPage).PullAsync([skewers], CancellationToken.None);

        pull.Outcome.Should().Be(PullOutcome.Failed);
        pull.Message.Should().Contain("page layout");
        pull.Listings[skewers.ListingId].IsUnlisted.Should().BeTrue("no reading is a failure of the listing itself");
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
    public void The_recorded_denial_page_is_what_the_fetcher_treats_as_a_block()
    {
        PoliteFetcher.LooksLikeBlockPage(Denied).Should().BeTrue();
        PoliteFetcher.LooksLikeBlockPage(Fixtures.Read("lidl-product-page.html")).Should().BeFalse();
    }
}
