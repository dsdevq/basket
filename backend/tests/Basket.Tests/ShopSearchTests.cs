using System.Net;
using Basket.Api.Prices;
using Basket.Api.Prices.Fetching;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>Searching a shop by name: Lidl's search page is read, the shops the VPS cannot reach are named, never tried.</summary>
public sealed class ShopSearchTests : IClassFixture<BasketFactory>
{
    private const string SearchUrl = "https://www.lidl.ie/q/search?q=milk chocolate";

    private readonly BasketFactory _factory;

    public ShopSearchTests(BasketFactory factory) => _factory = factory;

    [Fact]
    public async Task Lidl_results_carry_the_name_the_page_and_the_photo()
    {
        var fetcher = new FakeFetcher().Serve(SearchUrl, Fixtures.Read("lidl-search-page.html"));

        var response = await ShopSearch.RunAsync(fetcher, "milk chocolate", default);

        fetcher.Requested.Should().Equal(SearchUrl);
        response.Results.Should().HaveCount(4);
        response.Results[0].Should().Be(new ShopSearchResult(
            "lidl",
            "https://www.lidl.ie/p/milk-dark-chocolate-collection-selection/p11214707",
            "Milk & Dark Chocolate Collection Selection",
            "https://www.lidl.ie/assets/gcpc5d8e28a90974423b321241310a8f982.png"));
        response.Results.Should().OnlyContain(r => r.ImageUrl != null && r.Url.StartsWith("https://www.lidl.ie/p/"));
    }

    [Fact]
    public async Task The_other_shops_are_named_as_not_searched_and_are_never_requested()
    {
        var fetcher = new FakeFetcher().Serve(SearchUrl, Fixtures.Read("lidl-search-page.html"));

        var response = await ShopSearch.RunAsync(fetcher, "milk chocolate", default);

        response.Stores.Select(s => (s.Store, s.Searched)).Should().Equal(("lidl", true), ("aldi", false), ("tesco", false), ("dunnes", false));
        response.Stores.Skip(1).Should().OnlyContain(s => s.Note!.Contains("Paste a product link"));
        fetcher.Requested.Should().OnlyContain(u => u.StartsWith("https://www.lidl.ie/"));
    }

    [Fact]
    public async Task A_refused_or_unreachable_Lidl_gives_no_results_and_says_so()
    {
        var blocked = await ShopSearch.RunAsync(
            new FakeFetcher().Serve(SearchUrl, new FetchResult(FetchStatus.Blocked, null, "refused")), "milk chocolate", default);
        var down = await ShopSearch.RunAsync(
            new FakeFetcher().Serve(SearchUrl, new FetchResult(FetchStatus.Failed, null, "down")), "milk chocolate", default);

        blocked.Results.Should().BeEmpty();
        blocked.Stores[0].Should().Match<ShopSearchStatus>(s => !s.Searched && s.Note!.Contains("refused"));
        down.Results.Should().BeEmpty();
        down.Stores[0].Should().Match<ShopSearchStatus>(s => !s.Searched && s.Note!.Contains("could not be reached"));
    }

    [Fact]
    public async Task A_page_with_no_tiles_is_a_search_with_no_results()
    {
        var response = await ShopSearch.RunAsync(new FakeFetcher().Serve(SearchUrl, "<html></html>"), "milk chocolate", default);

        response.Results.Should().BeEmpty();
        response.Stores[0].Searched.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("m")]
    [InlineData("  \t ")]
    public void A_query_under_two_letters_is_refused(string? raw) => ShopSearch.TryCleanQuery(raw, out _).Should().BeFalse();

    [Fact]
    public async Task The_endpoint_refuses_a_short_query_and_an_anonymous_caller()
    {
        var (owner, _) = await _factory.OwnerAsync();

        (await owner.GetAsync("/api/products/search?q=m")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.NewBrowser().GetAsync("/api/products/search?q=milk")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
