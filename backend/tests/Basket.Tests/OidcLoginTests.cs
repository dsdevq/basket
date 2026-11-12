using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

/// <summary>The app with the org sign-in on, in front of the in-process provider.</summary>
public sealed class OidcBasketFactory : BasketFactory
{
    public OidcBasketFactory() => IdentityProvider = new FakeIdentityProvider();
}

/// <summary>
/// Sign-in through the org identity provider, end to end through the real OpenID Connect handler: start, the provider's
/// answer, the code exchange on the back channel, the account decision, basket's own session cookie, and sign-out.
/// </summary>
public sealed class OidcLoginTests(OidcBasketFactory factory) : OidcTestBase(factory), IClassFixture<OidcBasketFactory>
{
    // ---- start ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_sends_the_browser_to_the_public_authorize_endpoint_with_pkce_and_the_registered_redirect_uri()
    {
        var response = await _factory.NewBrowser().GetAsync("/api/auth/oidc/start");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith(FakeIdentityProvider.Authority + "/auth");
        location.Should().Contain("response_type=code")
            .And.Contain("client_id=" + FakeIdentityProvider.ClientId)
            .And.Contain("code_challenge_method=S256")
            .And.Contain("code_challenge=")
            .And.Contain("scope=openid email profile roles")
            .And.Contain("redirect_uri=" + Uri.EscapeDataString(BasketFactory.Origin + "/api/auth/oidc/signin"));
    }

    [Fact]
    public async Task The_flow_sets_its_own_cookies_and_none_of_the_defaults_other_apps_on_the_host_use()
    {
        var response = await _factory.NewBrowser().GetAsync("/api/auth/oidc/start");

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.Should().Contain(c => c.StartsWith("basket-oidc-correlation.", StringComparison.Ordinal) && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
        cookies.Should().Contain(c => c.StartsWith("basket-oidc-nonce.", StringComparison.Ordinal) && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
        cookies.Should().NotContain(c => c.Contains(".AspNetCore.", StringComparison.Ordinal) || c.StartsWith("Identity.External", StringComparison.Ordinal));
    }

    // ---- who may sign in -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_without_the_app_role_and_no_account_is_refused_and_nothing_is_created()
    {
        var browser = _factory.NewBrowser();
        var email = $"{Unique()}@example.test";
        var before = await _factory.WithDb(db => db.Users.CountAsync());

        var landed = await SignInAtProviderAsync(browser, new ProviderPerson("sub-" + Unique(), email, true, "Stranger", "some-other-app"));

        landed.Headers.Location!.ToString().Should().Be(AccessDenied);
        SetsSession(landed).Should().BeFalse();
        (await SessionAsync(browser)).GetProperty("authenticated").GetBoolean().Should().BeFalse();
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(before);
    }

    [Fact]
    public async Task An_unverified_email_neither_links_an_account_nor_creates_one_even_with_the_app_role()
    {
        var browser = _factory.NewBrowser();
        var email = $"{Unique()}@example.test";
        var existing = await SeedAccountAsync(email);
        var sub = "sub-" + Unique();
        var before = await _factory.WithDb(db => db.Users.CountAsync());

        var landed = await SignInAtProviderAsync(browser, new ProviderPerson(sub, email, false, "Someone", AppRole));

        landed.Headers.Location!.ToString().Should().Be(AccessDenied);
        SetsSession(landed).Should().BeFalse();
        (await LoginsOfAsync(existing.Id)).Should().BeEmpty();
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(before);
    }

    [Fact]
    public async Task A_verified_email_links_the_account_that_holds_it_and_signs_in_as_that_person_without_the_role()
    {
        var browser = _factory.NewBrowser();
        var email = $"{Unique()}@example.test";
        var existing = await SeedAccountAsync(email, Roles.Owner, "The Owner");
        var sub = "sub-" + Unique();

        var landed = await SignInAtProviderAsync(browser, new ProviderPerson(sub, email.ToUpperInvariant(), true, "Different Name"));

        SetsSession(landed).Should().BeTrue();
        var user = (await SessionAsync(browser)).GetProperty("user");
        user.GetProperty("id").GetGuid().Should().Be(existing.Id);
        user.GetProperty("displayName").GetString().Should().Be("The Owner");
        user.GetProperty("role").GetString().Should().Be("owner");
        (await LoginsOfAsync(existing.Id)).Should().ContainSingle(l => l.LoginProvider == "lifekit" && l.ProviderKey == sub);
    }

    [Fact]
    public async Task A_linked_account_that_has_no_email_takes_the_providers_verified_one()
    {
        var existing = await SeedAccountAsync(null, Roles.Member, "Legacy");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.AddLoginAsync((await users.FindByIdAsync(existing.Id.ToString()))!, new UserLoginInfo("lifekit", "sub-legacy-" + existing.Id, "lifekit"))).Succeeded.Should().BeTrue();
        }

        var browser = _factory.NewBrowser();
        var email = $"{Unique()}@example.test";
        await SignInAtProviderAsync(browser, new ProviderPerson("sub-legacy-" + existing.Id, email, true, "Legacy"));

        (await SessionAsync(browser)).GetProperty("user").GetProperty("email").GetString().Should().Be(email);
    }

    [Fact]
    public async Task A_second_subject_cannot_take_an_account_already_linked_to_another()
    {
        var email = $"{Unique()}@example.test";
        var existing = await SeedAccountAsync(email);
        var rightful = _factory.NewBrowser();
        await SignInAtProviderAsync(rightful, new ProviderPerson("sub-" + Unique(), email, true, "Rightful"));
        var before = await _factory.WithDb(db => db.Users.CountAsync());

        var intruder = _factory.NewBrowser();
        var landed = await SignInAtProviderAsync(intruder, new ProviderPerson("sub-" + Unique(), email, true, "Intruder", AppRole));

        landed.Headers.Location!.ToString().Should().Be(AccessDenied);
        SetsSession(landed).Should().BeFalse();
        (await LoginsOfAsync(existing.Id)).Should().ContainSingle();
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(before);
    }

    [Fact]
    public async Task Two_accounts_holding_the_same_email_are_never_guessed_between()
    {
        var email = $"{Unique()}@example.test";
        var one = await SeedAccountAsync(email);
        var two = await SeedAccountAsync(email);

        var landed = await SignInAtProviderAsync(_factory.NewBrowser(), new ProviderPerson("sub-" + Unique(), email, true, "Either", AppRole));

        landed.Headers.Location!.ToString().Should().Be(AccessDenied);
        (await LoginsOfAsync(one.Id)).Should().BeEmpty();
        (await LoginsOfAsync(two.Id)).Should().BeEmpty();
    }

    // ---- the handshake itself ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_callback_that_the_app_never_started_goes_back_to_sign_in_with_an_error_and_no_session()
    {
        var response = await _factory.NewBrowser().GetAsync("/api/auth/oidc/signin?code=forged&state=forged");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be(SignInPage + "?error=OIDC_FAILED");
        SetsSession(response).Should().BeFalse();
    }

    [Fact]
    public async Task An_id_token_from_another_issuer_is_refused_the_issuer_is_pinned()
    {
        var browser = _factory.NewBrowser();

        var landed = await SignInAtProviderAsync(browser, new ProviderPerson("sub-" + Unique(), $"{Unique()}@example.test", true, "Ana", AppRole), issuer: "https://elsewhere.test/oidc");

        landed.Headers.Location!.ToString().Should().Be(SignInPage + "?error=OIDC_FAILED");
        (await SessionAsync(browser)).GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    // ---- sign-out ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Signing_out_of_a_passkey_session_just_ends_it_with_no_content()
    {
        var (owner, _) = await _factory.OwnerAsync();

        var response = await owner.PostAsync("/api/auth/sign-out", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SessionAsync(owner)).GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Passkey_sign_in_keeps_working_beside_the_provider_and_has_no_email()
    {
        var (owner, _) = await _factory.OwnerAsync();

        var user = (await SessionAsync(owner)).GetProperty("user");

        user.GetProperty("role").GetString().Should().Be("owner");
        user.GetProperty("email").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

/// <summary>With no provider configured the path is off: no endpoint, no cookie, passkeys only, as before.</summary>
public sealed class OidcOffTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public OidcOffTests(BasketFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/auth/oidc/start")]
    [InlineData("/api/auth/oidc/start?returnUrl=/list")]
    [InlineData("/api/auth/oidc/callback")]
    [InlineData("/api/auth/oidc/signin?code=x&state=y")]
    public async Task The_oidc_endpoints_answer_404_and_set_no_cookie(string path)
    {
        var response = await _factory.NewBrowser().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task The_session_never_asks_to_link_when_the_provider_path_is_off()
    {
        var (owner, _) = await _factory.OwnerAsync();

        var session = await owner.GetFromJsonAsync<JsonElement>("/api/session");

        session.GetProperty("linkRequired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Signing_out_with_the_path_off_is_unchanged()
    {
        var (owner, _) = await _factory.OwnerAsync();

        (await owner.PostAsync("/api/auth/sign-out", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
