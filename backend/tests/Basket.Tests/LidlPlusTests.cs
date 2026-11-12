using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.LidlPlus;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

public sealed class LidlPlusTests : IClassFixture<BasketFactory>
{
    private const string Url = "/api/lidl-plus/connection";
    private const string Token = "A1B2C3D4E5F60718293A4B5C6D7E8F90-1";

    private readonly BasketFactory _factory;

    public LidlPlusTests(BasketFactory factory) => _factory = factory;

    private async Task<HttpClient> CleanOwnerAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.LidlPlusConnections.ExecuteDeleteAsync();
            return 0;
        });
        _factory.Lidl.Reset();
        return owner;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<string> StoredAsync() => _factory.WithDb(db => db.LidlPlusConnections.AsNoTracking().Select(c => c.RefreshTokenProtected).SingleAsync());

    private string Reveal(string stored) => _factory.Services.GetRequiredService<LidlPlusTokenProtector>().Unprotect(stored)!;

    [Fact]
    public async Task Nothing_is_connected_until_a_token_is_pasted()
    {
        var owner = await CleanOwnerAsync();

        var status = await Body(await owner.GetAsync(Url));

        status.GetProperty("state").GetString().Should().Be("not_connected");
        status.GetProperty("connectedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_pasted_token_that_Lidl_accepts_connects_and_a_second_paste_replaces_it()
    {
        var owner = await CleanOwnerAsync();

        var first = await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var status = await Body(first);
        status.GetProperty("state").GetString().Should().Be("connected");
        status.GetProperty("connectedAt").GetDateTimeOffset().Should().BeCloseTo(_factory.Clock.GetUtcNow(), TimeSpan.FromSeconds(5));
        _factory.Lidl.Asked.Should().Equal(Token);

        var second = await owner.PutAsJsonAsync(Url, new { refreshToken = Token + "9" });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.WithDb(db => db.LidlPlusConnections.CountAsync())).Should().Be(1);
        Reveal(await StoredAsync()).Should().Be(Token + "9");
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("connected");
    }

    [Fact]
    public async Task The_token_is_checked_with_Lidl_before_it_is_kept_and_surrounding_quotes_and_spaces_are_dropped()
    {
        var owner = await CleanOwnerAsync();

        (await owner.PutAsJsonAsync(Url, new { refreshToken = $"  \"{Token}\"\n" })).StatusCode.Should().Be(HttpStatusCode.Created);

        _factory.Lidl.Asked.Should().Equal(Token);
    }

    [Fact]
    public async Task The_token_is_encrypted_in_the_database_and_never_returned()
    {
        var owner = await CleanOwnerAsync();

        var response = await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        var stored = await StoredAsync();

        stored.Should().NotContain(Token).And.NotContain(Token[..12]);
        Reveal(stored).Should().Be(Token);
        foreach (var raw in new[] { await response.Content.ReadAsStringAsync(), await (await owner.GetAsync(Url)).Content.ReadAsStringAsync() })
        {
            raw.Should().NotContain(Token).And.NotContain(Token[..12]).And.NotContain(stored);
        }
    }

    [Fact]
    public async Task When_Lidl_hands_out_a_new_token_the_new_one_is_kept()
    {
        var owner = await CleanOwnerAsync();
        _factory.Lidl.Then(FakeLidlPlusAuth.Accepts(rotated: "ROTATED-TOKEN-0123456789-2"));

        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });

        Reveal(await StoredAsync()).Should().Be("ROTATED-TOKEN-0123456789-2");
    }

    [Fact]
    public async Task A_token_Lidl_refuses_is_not_kept_and_leaves_an_existing_connection_alone()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        _factory.Lidl.Then(LidlAuthResult.Rejected);

        var refused = await owner.PutAsJsonAsync(Url, new { refreshToken = "SOMETHING-ELSE-ENTIRELY-0000" });

        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await Body(refused);
        body.GetProperty("error").GetString().Should().Be("token_rejected");
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("SOMETHING-ELSE");
        Reveal(await StoredAsync()).Should().Be(Token);
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("connected");
    }

    [Fact]
    public async Task A_token_is_not_kept_when_Lidl_cannot_be_reached()
    {
        var owner = await CleanOwnerAsync();
        _factory.Lidl.Then(LidlAuthResult.Unavailable);

        var response = await owner.PutAsJsonAsync(Url, new { refreshToken = Token });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await Body(response)).GetProperty("error").GetString().Should().Be("lidl_unavailable");
        (await _factory.WithDb(db => db.LidlPlusConnections.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("has a space inside the token 0123456789")]
    [InlineData("tab\tinside-the-token-0123456789")]
    [InlineData("héllo-not-ascii-0123456789")]
    public async Task A_value_that_cannot_be_a_token_is_refused_without_asking_Lidl_or_repeating_it(string? value)
    {
        var owner = await CleanOwnerAsync();

        var response = await owner.PutAsJsonAsync(Url, new { refreshToken = value });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Body(response)).GetProperty("error").GetString().Should().Be("token_invalid");
        if (!string.IsNullOrWhiteSpace(value))
        {
            (await response.Content.ReadAsStringAsync()).Should().NotContain(value.Trim());
        }

        _factory.Lidl.Asked.Should().BeEmpty();
        (await _factory.WithDb(db => db.LidlPlusConnections.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_token_is_never_logged()
    {
        var owner = await CleanOwnerAsync();
        var secret = "S" + string.Concat(Guid.NewGuid().ToByteArray().Select(b => b.ToString("x2")));
        var rotated = "R" + string.Concat(Guid.NewGuid().ToByteArray().Select(b => b.ToString("x2")));

        _factory.Lidl.Then(LidlAuthResult.Rejected);
        await owner.PutAsJsonAsync(Url, new { refreshToken = secret });
        _factory.Lidl.Then(LidlAuthResult.Unavailable);
        await owner.PutAsJsonAsync(Url, new { refreshToken = secret });
        await owner.PutAsJsonAsync(Url, new { refreshToken = secret + " bad" });
        _factory.Lidl.Then(FakeLidlPlusAuth.Accepts(rotated: rotated));
        await owner.PutAsJsonAsync(Url, new { refreshToken = secret });
        await owner.GetAsync(Url);
        await owner.PostAsync(Url + "/check", null);
        var protectedValue = await StoredAsync();
        await owner.DeleteAsync(Url);

        // The run's own requests were logged (the capture works), and no token, in the clear or protected, is in any of them.
        _factory.Logs.Lines.Should().Contain(line => line.Contains(Url, StringComparison.Ordinal));
        _factory.Logs.Lines.Should().NotContain(line =>
            line.Contains(secret, StringComparison.Ordinal) || line.Contains(rotated, StringComparison.Ordinal) || line.Contains(protectedValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Answers_are_never_cached()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });

        foreach (var response in new[] { await owner.GetAsync(Url), await owner.PostAsync(Url + "/check", null), await owner.PutAsJsonAsync(Url, new { refreshToken = "no" }) })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Checking_asks_Lidl_with_the_stored_token_keeps_the_rotated_one_and_stamps_the_time()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        var connectedAt = (await Body(await owner.GetAsync(Url))).GetProperty("connectedAt").GetDateTimeOffset();
        _factory.Clock.Advance(TimeSpan.FromHours(3));
        _factory.Lidl.Asked.Clear();
        _factory.Lidl.Then(FakeLidlPlusAuth.Accepts(rotated: "ROTATED-TOKEN-0123456789-2"));

        var checkedNow = await owner.PostAsync(Url + "/check", null);

        checkedNow.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Lidl.Asked.Should().Equal(Token);
        Reveal(await StoredAsync()).Should().Be("ROTATED-TOKEN-0123456789-2");
        var status = await Body(checkedNow);
        status.GetProperty("state").GetString().Should().Be("connected");
        status.GetProperty("connectedAt").GetDateTimeOffset().Should().Be(connectedAt);
        status.GetProperty("verifiedAt").GetDateTimeOffset().Should().BeCloseTo(connectedAt + TimeSpan.FromHours(3), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task When_Lidl_refuses_the_stored_token_the_status_says_a_new_one_is_needed_and_Lidl_is_not_asked_again()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        _factory.Lidl.Asked.Clear();
        _factory.Lidl.Then(LidlAuthResult.Rejected);

        var checkedNow = await owner.PostAsync(Url + "/check", null);

        checkedNow.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(checkedNow)).GetProperty("state").GetString().Should().Be("needs_new_token");
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("needs_new_token");
        await owner.PostAsync(Url + "/check", null);
        _factory.Lidl.Asked.Should().HaveCount(1);

        // Pasting a fresh token puts it right.
        (await owner.PutAsJsonAsync(Url, new { refreshToken = Token + "2" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("connected");
    }

    [Fact]
    public async Task When_Lidl_cannot_be_reached_a_check_says_so_and_changes_nothing()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        _factory.Lidl.Then(LidlAuthResult.Unavailable);

        var checkedNow = await owner.PostAsync(Url + "/check", null);

        checkedNow.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        Reveal(await StoredAsync()).Should().Be(Token);
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("connected");
    }

    [Fact]
    public async Task A_check_with_nothing_connected_is_a_404()
    {
        var owner = await CleanOwnerAsync();

        (await owner.PostAsync(Url + "/check", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Lidl.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_that_cannot_be_decrypted_shows_as_unreadable_and_a_new_paste_replaces_it()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        await _factory.WithDb(db => db.LidlPlusConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.RefreshTokenProtected, "not-a-protected-value")));
        _factory.Lidl.Asked.Clear();

        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("unreadable");
        (await Body(await owner.PostAsync(Url + "/check", null))).GetProperty("state").GetString().Should().Be("unreadable");
        _factory.Lidl.Asked.Should().BeEmpty();

        (await owner.PutAsJsonAsync(Url, new { refreshToken = Token })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("connected");
    }

    [Fact]
    public async Task Disconnecting_removes_the_token()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });

        (await owner.DeleteAsync(Url)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _factory.WithDb(db => db.LidlPlusConnections.CountAsync())).Should().Be(0);
        (await Body(await owner.GetAsync(Url))).GetProperty("state").GetString().Should().Be("not_connected");
        (await owner.DeleteAsync(Url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Other_code_gets_an_access_token_and_the_rotated_refresh_token_is_saved_before_it_returns()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync(Url, new { refreshToken = Token });
        _factory.Lidl.Then(FakeLidlPlusAuth.Accepts("fresh-access", rotated: "ROTATED-TOKEN-0123456789-2"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var access = await scope.ServiceProvider.GetRequiredService<LidlPlusConnectionService>().AccessAsync(CancellationToken.None);

        access.State.Should().Be(LidlPlusState.Connected);
        access.AccessToken.Should().Be("fresh-access");
        Reveal(await StoredAsync()).Should().Be("ROTATED-TOKEN-0123456789-2");
        access.ToString().Should().NotContain("fresh-access");
    }

    [Fact]
    public async Task Without_a_connection_there_is_no_access_token_and_Lidl_is_not_asked()
    {
        await CleanOwnerAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var access = await scope.ServiceProvider.GetRequiredService<LidlPlusConnectionService>().AccessAsync(CancellationToken.None);

        access.State.Should().Be(LidlPlusState.NotConnected);
        access.AccessToken.Should().BeNull();
        _factory.Lidl.Asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0123456789abcdef", "0123456789abcdef")]
    [InlineData("  \"0123456789abcdef\"  ", "0123456789abcdef")]
    [InlineData("'0123456789abcdef'", "0123456789abcdef")]
    [InlineData("0123456789abcde", null)]
    [InlineData("0123456789 abcdef", null)]
    [InlineData("", null)]
    public void Pasted_text_becomes_a_token_only_when_it_could_be_one(string raw, string? expected) =>
        LidlPlusTokens.Normalize(raw).Should().Be(expected);

    [Fact]
    public void A_token_longer_than_any_Lidl_issues_is_refused() =>
        LidlPlusTokens.Normalize(new string('a', LidlPlusTokens.MaxLength + 1)).Should().BeNull();
}
