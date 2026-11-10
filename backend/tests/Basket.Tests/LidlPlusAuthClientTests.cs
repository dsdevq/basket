using System.Net;
using System.Text;
using Basket.Api.LidlPlus;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Basket.Tests;

/// <summary>Lidl's token endpoint as recorded answers: what basket sends, and how each kind of answer is read. No request leaves the process.</summary>
public sealed class LidlPlusAuthClientTests
{
    private const string Token = "A1B2C3D4E5F60718293A4B5C6D7E8F90-1";

    private sealed class Recorded(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (LidlPlusAuthClient Client, Recorded Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Recorded(answer);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://accounts.lidl.test/") };
        return (new LidlPlusAuthClient(http, Options.Create(new LidlPlusOptions())), handler);
    }

    [Fact]
    public async Task It_posts_the_token_in_the_body_to_the_token_endpoint_as_the_app_does()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, """{"access_token":"a","refresh_token":"r","expires_in":1800,"token_type":"Bearer"}"""));

        await client.RefreshAsync(Token, CancellationToken.None);

        var (request, body) = handler.Seen.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://accounts.lidl.test/connect/token").And.NotContain(Token);
        body.Split('&').Should().BeEquivalentTo("grant_type=refresh_token", "refresh_token=" + Token);
        request.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.ASCII.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)).Should().Be("LidlPlusNativeClient:secret");
    }

    [Fact]
    public async Task An_accepted_token_gives_the_access_token_and_the_replacement_refresh_token()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, """{"access_token":"the-access","refresh_token":"the-new-refresh","expires_in":1800}"""));

        var result = await client.RefreshAsync(Token, CancellationToken.None);

        result.Outcome.Should().Be(LidlAuthOutcome.Accepted);
        result.AccessToken.Should().Be("the-access");
        result.RefreshToken.Should().Be("the-new-refresh");
        result.ExpiresInSeconds.Should().Be(1800);
        result.ToString().Should().NotContain("the-access").And.NotContain("the-new-refresh");
    }

    [Fact]
    public async Task An_answer_without_a_new_refresh_token_means_Lidl_kept_the_old_one()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, """{"access_token":"the-access"}"""));

        var result = await client.RefreshAsync(Token, CancellationToken.None);

        result.Outcome.Should().Be(LidlAuthOutcome.Accepted);
        result.RefreshToken.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    public async Task A_token_Lidl_refuses_is_rejected(HttpStatusCode status, string body)
    {
        var (client, _) = Client(_ => Json(status, body));

        (await client.RefreshAsync(Token, CancellationToken.None)).Outcome.Should().Be(LidlAuthOutcome.Rejected);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.BadGateway, "")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    [InlineData(HttpStatusCode.OK, "<html>maintenance</html>")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, """{"refresh_token":"only-a-refresh"}""")]
    [InlineData(HttpStatusCode.OK, """{"access_token":""}""")]
    [InlineData(HttpStatusCode.OK, """{"access_token":42}""")]
    [InlineData(HttpStatusCode.OK, """["access_token"]""")]
    public async Task An_answer_that_is_not_a_verdict_leaves_the_token_untouched(HttpStatusCode status, string body)
    {
        var (client, _) = Client(_ => Json(status, body));

        (await client.RefreshAsync(Token, CancellationToken.None)).Outcome.Should().Be(LidlAuthOutcome.Unavailable);
    }

    [Fact]
    public async Task A_network_failure_is_unavailable_and_its_message_is_not_kept()
    {
        var (client, _) = Client(_ => throw new HttpRequestException("connection refused while sending " + Token));

        var result = await client.RefreshAsync(Token, CancellationToken.None);

        result.Outcome.Should().Be(LidlAuthOutcome.Unavailable);
        result.ToString().Should().NotContain(Token);
    }

    [Fact]
    public async Task A_timeout_is_unavailable_but_a_cancelled_caller_is_not_swallowed()
    {
        var (timedOut, _) = Client(_ => throw new TaskCanceledException("timed out"));
        (await timedOut.RefreshAsync(Token, CancellationToken.None)).Outcome.Should().Be(LidlAuthOutcome.Unavailable);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var (client, _) = Client(_ => throw new TaskCanceledException("cancelled"));
        var act = () => client.RefreshAsync(Token, cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
