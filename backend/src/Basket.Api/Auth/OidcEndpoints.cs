using System.Security.Claims;
using Basket.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Basket.Api.Auth;

public sealed record EndSessionResponse(string EndSessionUrl);

/// <summary>
/// The browser half of the org sign-in: <c>start</c> sends the person to the provider, the OIDC handler completes the
/// code exchange at <see cref="OidcLoginOptions.CallbackPath"/> and hands over to <c>callback</c>, which resolves the
/// account (linking a passkey account that holds a valid session) and opens basket's own session cookie. Every answer to a person is a redirect (these are page navigations);
/// when the path is off they are 404s.
/// </summary>
public static class OidcEndpoints
{
    private const string ReturnUrlItem = "returnUrl";
    private const string IdTokenCookie = "__Host-basket-oidc-id";
    private const string IdTokenPurpose = "basket.oidc.id-token";

    public static void MapOidcEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapGet("/oidc/start", Start);
        auth.MapGet("/oidc/callback", Callback);
    }

    private static IResult Disabled() =>
        Results.Json(new { error = "oidc_disabled", message = "Sign-in with the lifekit account is not set up here." }, statusCode: 404);

    private static IResult Start(string? returnUrl, IOptions<OidcLoginOptions> options)
    {
        if (!options.Value.IsConfigured)
        {
            return Disabled();
        }

        var properties = new AuthenticationProperties { RedirectUri = OidcLoginOptions.AccountPath };
        if (IsLocalPath(returnUrl))
        {
            properties.Items[ReturnUrlItem] = returnUrl;
        }

        return Results.Challenge(properties, [OidcLoginOptions.Scheme]);
    }

    private static async Task<IResult> Callback(
        HttpContext http,
        IOptions<OidcLoginOptions> options,
        ExternalLoginService accounts,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IDataProtectionProvider protection)
    {
        var oidc = options.Value;
        if (!oidc.IsConfigured)
        {
            return Disabled();
        }

        var external = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
        await http.SignOutAsync(IdentityConstants.ExternalScheme);

        var subject = external.Principal?.FindFirstValue("sub");
        if (!external.Succeeded || string.IsNullOrWhiteSpace(subject))
        {
            return Results.Redirect(OidcLoginExtensions.LoginFailureUrl(oidc.PublicBaseUrl, OidcLoginExtensions.FailureRedirectCode));
        }

        var principal = external.Principal!;
        var identity = new ExternalIdentity(
            subject,
            principal.FindFirstValue("email"),
            bool.TryParse(principal.FindFirstValue("email_verified"), out var verified) && verified,
            principal.FindFirstValue("name"),
            oidc.GrantsApp(principal.FindAll(OidcLoginOptions.RolesClaim).Select(c => c.Value)));

        // A valid basket session, if the request carries one: it is what lets a passkey account that predates the provider link to it.
        var session = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var sessionUser = session.Succeeded && session.Principal is not null ? await users.GetUserAsync(session.Principal) : null;

        var result = await accounts.ResolveAsync(identity, sessionUser, http.RequestAborted);
        if (result.User is not { } user)
        {
            return Results.Redirect(result.Refusal == ExternalLoginRefusal.LinkRequired ? oidc.LinkRequiredUrl : oidc.AccessDeniedUrl);
        }

        await signIn.SignInAsync(user, isPersistent: true);
        if (external.Properties?.GetTokenValue(OpenIdConnectParameterNames.IdToken) is { Length: > 0 } idToken)
        {
            WriteIdToken(http, protection, idToken, http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.SessionDays);
        }

        string? returnUrl = null;
        external.Properties?.Items.TryGetValue(ReturnUrlItem, out returnUrl);
        return Results.Redirect(oidc.PublicBaseUrl.TrimEnd('/') + (IsLocalPath(returnUrl) ? returnUrl : "/"));
    }

    /// <summary>
    /// Ends the provider's session too (RP-initiated logout with the <c>id_token_hint</c> kept at sign-in): the answer is
    /// the URL the SPA navigates to, or null when this session did not come from the provider (or the path is off).
    /// </summary>
    internal static async Task<EndSessionResponse?> EndProviderSessionAsync(
        HttpContext http, OidcLoginOptions oidc, IDataProtectionProvider protection, ILogger log)
    {
        var idToken = ReadIdToken(http, protection);
        http.Response.Cookies.Delete(IdTokenCookie, new CookieOptions { Secure = true, Path = "/" });
        if (!oidc.IsConfigured || idToken is null)
        {
            return null;
        }

        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = OpenIdConnectParameterNames.IdToken, Value = idToken }]);
        try
        {
            await http.SignOutAsync(OidcLoginOptions.Scheme, properties);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not build the identity provider's end-session URL; the local sign-out still completed");
            return null;
        }

        return http.Items[OidcLoginExtensions.EndSessionUrlItem] is string url ? new EndSessionResponse(url) : null;
    }

    private static void WriteIdToken(HttpContext http, IDataProtectionProvider protection, string idToken, int days)
    {
        http.Response.Cookies.Append(IdTokenCookie, protection.CreateProtector(IdTokenPurpose).Protect(idToken), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(days),
            IsEssential = true,
        });
    }

    private static string? ReadIdToken(HttpContext http, IDataProtectionProvider protection)
    {
        if (!http.Request.Cookies.TryGetValue(IdTokenCookie, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return protection.CreateProtector(IdTokenPurpose).Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    // Only an absolute path on this app is a valid return target: a scheme-relative ("//host") or backslash form
    // would be an open redirect.
    internal static bool IsLocalPath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.StartsWith("/\\", StringComparison.Ordinal);
}
