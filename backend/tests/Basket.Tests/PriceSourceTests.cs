using Basket.Api.Prices.Fetching;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>What the fetcher treats as a refusal, on a recorded denial page.</summary>
public sealed class PriceSourceTests
{
    private static readonly string Denied = Fixtures.Read("akamai-access-denied.html");

    [Fact]
    public void The_recorded_denial_page_is_what_the_fetcher_treats_as_a_block()
    {
        PoliteFetcher.LooksLikeBlockPage(Denied).Should().BeTrue();
        PoliteFetcher.LooksLikeBlockPage(Fixtures.Read("lidl-product-page.html")).Should().BeFalse();
    }
}
