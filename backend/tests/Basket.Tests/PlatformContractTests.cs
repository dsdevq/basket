using System.Net;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>The platform contract's endpoints (lifekit-stack docs/platform-contract.md): health, readiness, metrics, logs.</summary>
public sealed class PlatformContractTests : IAsyncLifetime
{
    private readonly BasketFactory _factory = new() { Metrics = true };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/health/ready")]
    public async Task Health_and_readiness_answer_anonymously_with_something_that_is_not_html(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task Metrics_are_Prometheus_text_and_need_no_sign_in()
    {
        using var client = _factory.CreateClient();
        await client.GetAsync("/api/health");

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().StartWith("text/plain");
        (await response.Content.ReadAsStringAsync()).Should().Contain("service_name=\"basket\"").And.Contain("http_server");
    }

    [Fact]
    public async Task Metrics_are_not_served_to_a_request_that_came_through_the_tailscale_proxy()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("http_server");
    }

    [Fact]
    public async Task A_health_probe_that_sends_trace_context_is_logged_one_that_does_not_is_not()
    {
        using var client = _factory.CreateClient();
        var quiet = $"/api/health/quiet-{Guid.NewGuid():N}";
        var traced = $"/api/health/traced-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Get, traced);
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        await client.GetAsync(quiet);
        await client.SendAsync(request);

        _factory.Logs.Lines.Should().Contain(line => line.Contains(traced, StringComparison.Ordinal));
        _factory.Logs.Lines.Should().NotContain(line => line.Contains(quiet, StringComparison.Ordinal));
    }
}
