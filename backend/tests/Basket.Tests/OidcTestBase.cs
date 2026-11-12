using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

/// <summary>What the sign-in test classes share: the browser's walk through the provider and the account seeding. Each class has its own app and database.</summary>
public abstract class OidcTestBase(OidcBasketFactory factory)
{
    protected const string AppRole = "basket";
    protected const string AccessDenied = BasketFactory.Origin + "/access-denied";
    protected const string LinkRequired = BasketFactory.Origin + "/link-required";
    protected const string SignInPage = BasketFactory.Origin + "/sign-in";

    protected readonly OidcBasketFactory _factory = factory;

    protected FakeIdentityProvider Provider => _factory.IdentityProvider!;

    /// <summary>Start, "sign in at the provider", come back with its code, and land on the account step. Returns that step's answer.</summary>
    protected async Task<HttpResponseMessage> SignInAtProviderAsync(
        HttpClient browser, ProviderPerson person, string? returnUrl = "/list", string issuer = FakeIdentityProvider.Authority)
    {
        var start = await browser.GetAsync("/api/auth/oidc/start" + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);

        var code = Provider.IssueCode(person, query["nonce"]!, query["code_challenge"]!, issuer);
        var signin = await browser.GetAsync($"/api/auth/oidc/signin?code={code}&state={Uri.EscapeDataString(query["state"]!)}");
        signin.StatusCode.Should().Be(HttpStatusCode.Redirect);
        if (signin.Headers.Location!.ToString().Contains("/sign-in?error=", StringComparison.Ordinal))
        {
            return signin;
        }

        return await browser.GetAsync(signin.Headers.Location);
    }

    protected static string Unique() => Guid.NewGuid().ToString("N")[..10];

    protected static bool SetsSession(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("__Host-basket=", StringComparison.Ordinal));

    protected async Task<AppUser> SeedAccountAsync(string? email, string role = Roles.Member, string name = "Seeded")
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var id = Guid.NewGuid();
        var user = new AppUser { Id = id, UserName = id.ToString("N"), Email = email, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        return user;
    }

    protected async Task<IList<UserLoginInfo>> LoginsOfAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.GetLoginsAsync((await users.FindByIdAsync(userId.ToString()))!);
    }

    protected static async Task<JsonElement> SessionAsync(HttpClient browser) =>
        await browser.GetFromJsonAsync<JsonElement>("/api/session");
}
