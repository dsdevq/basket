using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// Sign-in by a provider person who has no account yet. Creating a Member is only allowed while every account already has a provider
/// login, so this class's app never holds a passkey-only account (the linking class is the one that does).
/// </summary>
public sealed class OidcMemberCreationTests(OidcBasketFactory factory) : OidcTestBase(factory), IClassFixture<OidcBasketFactory>
{
    [Theory]
    [InlineData("//evil.test/x")]
    [InlineData("/\\evil.test")]
    [InlineData("https://evil.test/")]
    [InlineData("list")]
    public async Task A_return_url_that_leaves_the_app_is_ignored(string returnUrl)
    {
        var browser = _factory.NewBrowser();

        var landed = await SignInAtProviderAsync(browser, new ProviderPerson("sub-" + Unique(), $"{Unique()}@example.test", true, "Ana", AppRole), returnUrl);

        landed.Headers.Location!.ToString().Should().Be(BasketFactory.Origin + "/");
    }

    [Fact]
    public async Task A_person_with_the_app_role_and_no_account_becomes_a_member_with_a_session()
    {
        var browser = _factory.NewBrowser();
        var email = $"{Unique()}@example.test";

        var landed = await SignInAtProviderAsync(browser, new ProviderPerson("sub-" + Unique(), email, true, "Ana Kovalenko", "other-app", AppRole), "/list");

        landed.Headers.Location!.ToString().Should().Be(BasketFactory.Origin + "/list");
        SetsSession(landed).Should().BeTrue();
        var session = await SessionAsync(browser);
        session.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        var user = session.GetProperty("user");
        user.GetProperty("displayName").GetString().Should().Be("Ana Kovalenko");
        user.GetProperty("role").GetString().Should().Be("member");
        user.GetProperty("email").GetString().Should().Be(email);

        var stored = await _factory.WithDb(db => db.Users.SingleAsync(u => u.Email == email));
        stored.EmailConfirmed.Should().BeTrue();
        (await LoginsOfAsync(stored.Id)).Should().ContainSingle(l => l.LoginProvider == "lifekit");
    }

    [Fact]
    public async Task The_display_name_is_the_local_part_of_the_email_when_the_provider_sends_no_name()
    {
        var browser = _factory.NewBrowser();
        var local = "gf" + Unique();

        await SignInAtProviderAsync(browser, new ProviderPerson("sub-" + Unique(), $"{local}@example.test", true, null, AppRole));

        (await SessionAsync(browser)).GetProperty("user").GetProperty("displayName").GetString().Should().Be(local);
    }

    [Fact]
    public async Task A_linked_subject_signs_in_again_to_the_same_account_with_no_role_and_no_email()
    {
        var sub = "sub-" + Unique();
        var email = $"{Unique()}@example.test";
        var first = _factory.NewBrowser();
        await SignInAtProviderAsync(first, new ProviderPerson(sub, email, true, "Ana", AppRole));
        var id = (await SessionAsync(first)).GetProperty("user").GetProperty("id").GetGuid();
        var before = await _factory.WithDb(db => db.Users.CountAsync());

        var second = _factory.NewBrowser();
        var landed = await SignInAtProviderAsync(second, new ProviderPerson(sub, null, false, null));

        SetsSession(landed).Should().BeTrue();
        (await SessionAsync(second)).GetProperty("user").GetProperty("id").GetGuid().Should().Be(id);
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(before);
    }

    [Fact]
    public async Task Every_server_side_call_to_the_provider_goes_over_the_back_channel()
    {
        await SignInAtProviderAsync(_factory.NewBrowser(), new ProviderPerson("sub-" + Unique(), $"{Unique()}@example.test", true, "Ana", AppRole));

        Provider.Requests.Should().NotBeEmpty();
        Provider.Requests.Should().OnlyContain(u => u.Host == "logto.test" && u.Port == 3001);
        Provider.Requests.Select(u => u.AbsolutePath).Should().Contain(["/oidc/.well-known/openid-configuration", "/oidc/jwks", "/oidc/token", "/oidc/me"]);
    }

    [Fact]
    public async Task Signing_out_of_a_provider_session_returns_the_end_session_url_with_the_id_token_hint()
    {
        var browser = _factory.NewBrowser();
        await SignInAtProviderAsync(browser, new ProviderPerson("sub-" + Unique(), $"{Unique()}@example.test", true, "Ana", AppRole));
        var idToken = Provider.LastIdToken!;

        var response = await browser.PostAsync("/api/auth/sign-out", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endSessionUrl").GetString()!);
        url.GetLeftPart(UriPartial.Path).Should().Be(FakeIdentityProvider.Authority + "/session/end");
        var query = QueryHelpers.ParseQuery(url.Query);
        query["id_token_hint"].ToString().Should().Be(idToken);
        query["client_id"].ToString().Should().Be(FakeIdentityProvider.ClientId);
        query["post_logout_redirect_uri"].ToString().Should().Be(SignInPage + "?info=signed_out");
        (await SessionAsync(browser)).GetProperty("authenticated").GetBoolean().Should().BeFalse();
        // The hint cookie is cleared with the session.
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("__Host-basket-oidc-id=;", StringComparison.Ordinal));
    }
}
