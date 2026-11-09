using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

public sealed class AuthGuardTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public AuthGuardTests(BasketFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET", "/api/list")]
    [InlineData("POST", "/api/list/items")]
    [InlineData("PATCH", "/api/list/items/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/list/items/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/list/close")]
    [InlineData("GET", "/api/trips")]
    [InlineData("POST", "/api/trips/00000000-0000-0000-0000-000000000001/reload")]
    [InlineData("GET", "/api/products")]
    [InlineData("POST", "/api/products")]
    [InlineData("DELETE", "/api/products/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/api/products/00000000-0000-0000-0000-000000000001/stores/lidl")]
    [InlineData("GET", "/api/invites")]
    [InlineData("POST", "/api/invites")]
    [InlineData("GET", "/api/members")]
    [InlineData("GET", "/api/ingest-tokens")]
    [InlineData("POST", "/api/ingest-tokens")]
    [InlineData("DELETE", "/api/ingest-tokens/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/ingest/prices")]
    [InlineData("POST", "/api/auth/sign-out")]
    [InlineData("GET", "/api/passkeys")]
    [InlineData("POST", "/api/passkeys/options")]
    [InlineData("POST", "/api/passkeys")]
    [InlineData("DELETE", "/api/passkeys/AAAA")]
    public async Task Anonymous_callers_are_refused_with_401_not_a_redirect(string method, string path)
    {
        var client = _factory.NewBrowser();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Health_and_session_are_open_and_the_session_says_nobody_is_signed_in()
    {
        var client = _factory.NewBrowser();

        (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        session.GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Members_cannot_use_the_owner_endpoints()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var (member, _) = await _factory.MemberAsync(owner);

        (await member.GetAsync("/api/invites")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/invites", new { label = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.GetAsync("/api/ingest-tokens")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/ingest-tokens", new { label = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://basket.test.evil.example")]
    [InlineData("http://basket.test")]
    public async Task State_changing_requests_from_another_origin_are_refused_even_with_a_valid_session(string origin)
    {
        var (owner, _) = await _factory.OwnerAsync();
        owner.DefaultRequestHeaders.Remove("Origin");

        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/list/items") { Content = JsonContent.Create(new { text = "milk" }) };
        forged.Headers.Add("Origin", origin);

        (await owner.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_state_changing_request_without_an_origin_is_refused()
    {
        var (owner, _) = await _factory.OwnerAsync();
        owner.DefaultRequestHeaders.Remove("Origin");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/list/items") { Content = JsonContent.Create(new { text = "milk" }) };

        (await owner.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_session_cookie_is_host_prefixed_httponly_secure_and_samesite_strict()
    {
        await using var fresh = new BasketFactory();
        var client = fresh.NewBrowser();
        var passkey = new FakeAuthenticator(BasketFactory.Origin);
        var options = await client.PostAsJsonAsync("/api/auth/register/options", new { token = BasketFactory.BootstrapToken, displayName = "Owner" });
        var done = await client.PostAsJsonAsync("/api/auth/register", new
        {
            token = BasketFactory.BootstrapToken,
            credentialJson = passkey.Create(await options.Content.ReadAsStringAsync()),
        });

        var cookie = done.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-basket=", StringComparison.Ordinal)).ToLowerInvariant();
        cookie.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/");
        cookie.Should().NotContain("domain=");
    }

    [Fact]
    public async Task Responses_carry_the_hardening_headers_and_api_answers_are_not_cacheable()
    {
        var response = await _factory.NewBrowser().GetAsync("/api/health");

        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'").And.Contain("object-src 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle("no-referrer");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_api_routes_are_404_and_do_not_fall_through_to_the_app_shell()
    {
        var response = await _factory.NewBrowser().GetAsync("/api/nope");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_sign_in_endpoints_are_rate_limited_per_client()
    {
        await using var limited = new BasketFactory { AuthRequestsPerMinute = 3 };
        var client = limited.NewBrowser();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/invite", new { token = "nope" })).StatusCode);
        }

        statuses.Should().Equal(
            HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound,
            HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests);
    }
}
