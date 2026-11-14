using System.IO.Compression;
using System.Net;
using System.Text;
using Basket.Api.Prices;
using Basket.Api.Prices.Fetching;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Basket.Tests;

public sealed class PoliteFetcherTests
{
    private static readonly Uri Page = new("https://www.lidl.ie/p/aubergine/p11214436");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (PoliteFetcher Fetcher, StubHandler Handler, FakeTimeProvider Clock) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T03:00:00Z"));
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("basket-price-check/1.0");
        var throttle = new HostThrottle(Options.Create(new PricesOptions { MinRequestIntervalSeconds = 5 }), clock);
        return (new PoliteFetcher(http, throttle, NullLogger<PoliteFetcher>.Instance), handler, clock);
    }

    [Fact]
    public async Task A_normal_answer_is_returned_as_the_page()
    {
        var (fetcher, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ok</html>") });

        var result = await fetcher.GetAsync(Page, CancellationToken.None);

        result.Status.Should().Be(FetchStatus.Ok);
        result.Body.Should().Be("<html>ok</html>");
        handler.Requests.Single().Headers.UserAgent.ToString().Should().StartWith("basket-price-check/");
    }

    [Fact]
    public async Task A_gzipped_sitemap_file_is_unpacked_into_text()
    {
        var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes("<urlset><url><loc>https://www.lidl.ie/c/food-drink/s1</loc></url></urlset>"));
        }

        var (fetcher, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packed.ToArray()) });

        var result = await fetcher.GetAsync(new Uri("https://www.lidl.ie/static/pages_en-IE_ie.xml.gz"), CancellationToken.None);

        result.Status.Should().Be(FetchStatus.Ok);
        result.Body.Should().Contain("<loc>https://www.lidl.ie/c/food-drink/s1</loc>");
    }

    [Fact]
    public async Task A_gz_address_that_is_not_gzip_is_a_failure_not_an_exception()
    {
        var (fetcher, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("plain text") });

        var result = await fetcher.GetAsync(new Uri("https://www.lidl.ie/static/pages_en-IE_ie.xml.gz"), CancellationToken.None);

        result.Status.Should().Be(FetchStatus.Failed);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refusal_is_reported_as_blocked_and_not_retried(HttpStatusCode code)
    {
        var (fetcher, handler, _) = Create(_ => new HttpResponseMessage(code));

        var result = await fetcher.GetAsync(Page, CancellationToken.None);

        result.Status.Should().Be(FetchStatus.Blocked);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_access_denied_page_served_with_200_is_blocked_too()
    {
        var (fetcher, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Read("akamai-access-denied.html")) });

        (await fetcher.GetAsync(Page, CancellationToken.None)).Status.Should().Be(FetchStatus.Blocked);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, FetchStatus.NotFound)]
    [InlineData(HttpStatusCode.Gone, FetchStatus.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, FetchStatus.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FetchStatus.Failed)]
    public async Task Other_statuses_are_told_apart(HttpStatusCode code, FetchStatus expected)
    {
        var (fetcher, _, _) = Create(_ => new HttpResponseMessage(code));

        (await fetcher.GetAsync(Page, CancellationToken.None)).Status.Should().Be(expected);
    }

    [Fact]
    public async Task A_network_error_is_a_failure_not_an_exception()
    {
        var (fetcher, _, _) = Create(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "boom"));

        var result = await fetcher.GetAsync(Page, CancellationToken.None);

        result.Status.Should().Be(FetchStatus.Failed);
        result.Detail.Should().Contain("www.lidl.ie");
    }

    [Fact]
    public async Task Requests_to_one_site_are_spaced_out_and_other_sites_are_not_held_back()
    {
        var (fetcher, handler, clock) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") });

        await fetcher.GetAsync(Page, CancellationToken.None);
        var second = fetcher.GetAsync(Page, CancellationToken.None);
        var other = await fetcher.GetAsync(new Uri("https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541"), CancellationToken.None);

        other.Status.Should().Be(FetchStatus.Ok);
        second.IsCompleted.Should().BeFalse("the second request to the same site waits for its slot");
        handler.Requests.Should().HaveCount(2);

        clock.Advance(TimeSpan.FromSeconds(5));
        (await second).Status.Should().Be(FetchStatus.Ok);
        handler.Requests.Should().HaveCount(3);
    }
}
