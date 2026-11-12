namespace Basket.Api.Auth;

/// <summary>
/// Sign-in through the org identity provider (Logto), bound from <c>Auth:Oidc</c> and shaped like finance-sentry's. The
/// whole path is off until <see cref="Authority"/>, <see cref="ClientId"/>, <see cref="ClientSecret"/> and
/// <see cref="PublicBaseUrl"/> are all set (see <see cref="IsConfigured"/>): nothing is registered, the endpoints answer
/// 404 and sign-in stays passkey-only, exactly as before.
/// </summary>
public sealed class OidcLoginOptions
{
    public const string Section = "Auth:Oidc";

    /// <summary>The authentication scheme name, and the provider name stored in <c>user_logins</c>.</summary>
    public const string Scheme = "lifekit";

    /// <summary>The path the provider returns the browser to; the OIDC handler completes the code exchange there.</summary>
    public const string CallbackPath = "/api/auth/oidc/signin";

    /// <summary>Where the browser lands once the handler has signed the provider's answer into the external cookie.</summary>
    public const string AccountPath = "/api/auth/oidc/callback";

    /// <summary>The claim (and the scope that asks for it) carrying the names of the person's provider roles.</summary>
    public const string RolesClaim = "roles";

    /// <summary>The SPA page a refused sign-in lands on.</summary>
    public const string AccessDeniedPath = "/access-denied";

    /// <summary>
    /// The SPA page a sign-in lands on when the person holds the app role but some basket account has no provider login yet: it asks
    /// them to open basket on the phone they already use, or sign in with their passkey, first.
    /// </summary>
    public const string LinkRequiredPath = "/link-required";

    /// <summary>The SPA page a failed sign-in, and a finished sign-out, land on.</summary>
    public const string SignInPath = "/sign-in";

    /// <summary>The issuer, as the tokens carry it, e.g. <c>https://host.tailnet.ts.net:3001/oidc</c>. Pinned: no other issuer is accepted.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Optional address basket itself uses for discovery, signing keys, token and userinfo calls, e.g.
    /// <c>http://logto:3001/oidc</c> on the shared Docker network when the container cannot reach the provider's public
    /// (tailnet) address. The issuer validated and the browser's authorize redirect keep using <see cref="Authority"/>.
    /// Empty = call the authority directly.
    /// </summary>
    public string BackchannelAuthority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>The confidential client's secret. It comes from the deploy env file, never from git.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The origin the browser reaches the app on (e.g. <c>https://host.tailnet.ts.net:3310</c>). The redirect URI registered
    /// with the provider and every post-sign-in redirect are built from it: the app sits behind a proxy that does not
    /// preserve the public scheme and port, so it cannot derive them from the request.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Turn off only for a local, plain-HTTP provider; otherwise keep HTTPS metadata. Ignored (treated as off) when
    /// <see cref="BackchannelAuthority"/> is plain HTTP, because the provider then advertises its keys on that address.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The provider role that grants this app. A first sign-in with a verified email and no matching account creates a
    /// Member only when the person holds it; an existing account signs in without it. Empty = no sign-in creates an account.
    /// </summary>
    public string RequiredRole { get; set; } = "basket";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _);

    /// <summary>The absolute redirect URI to register with the provider.</summary>
    public string RedirectUri => PublicBaseUrl.TrimEnd('/') + CallbackPath;

    /// <summary>
    /// Where the provider returns the browser after ending its session; register it exactly as the app's post sign-out
    /// redirect URI. The <c>info</c> value holds the sign-in page on its message instead of forwarding straight back.
    /// </summary>
    public string PostLogoutRedirectUri => PublicBaseUrl.TrimEnd('/') + SignInPath + "?info=signed_out";

    /// <summary>Where a sign-in the app refuses (no account and no app role, or an unverified email) sends the browser.</summary>
    public string AccessDeniedUrl => PublicBaseUrl.TrimEnd('/') + AccessDeniedPath;

    /// <summary>Where a sign-in that must link an existing account first sends the browser.</summary>
    public string LinkRequiredUrl => PublicBaseUrl.TrimEnd('/') + LinkRequiredPath;

    /// <summary>Whether the provider's role claims grant this app (<see cref="RequiredRole"/>).</summary>
    public bool GrantsApp(IEnumerable<string> roles) =>
        !string.IsNullOrWhiteSpace(RequiredRole) && roles.Contains(RequiredRole, StringComparer.Ordinal);
}
