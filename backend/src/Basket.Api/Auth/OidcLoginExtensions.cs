using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Basket.Api.Auth;

/// <summary>
/// Registers the org identity provider as an OpenID Connect scheme that signs into Identity's external cookie
/// (authorization code with PKCE, confidential client). Registers nothing unless <c>Auth:Oidc</c> is fully configured, so
/// an unconfigured deployment behaves exactly as before. The session itself stays basket's own Identity cookie.
/// </summary>
public static class OidcLoginExtensions
{
    public const string FailureRedirectCode = "OIDC_FAILED";

    /// <summary><c>HttpContext.Items</c> key under which the sign-out event leaves the provider's end-session URL.</summary>
    public const string EndSessionUrlItem = "oidc.end_session_url";

    // Every cookie of this flow has a name of its own: browsers share cookies across the ports of one host, and the
    // defaults (Identity.External, .AspNetCore.Correlation.*) are the ones other apps on the same host use.
    public const string ExternalCookieName = "basket-oidc-external";
    public const string CorrelationCookieName = "basket-oidc-correlation.";
    public const string NonceCookieName = "basket-oidc-nonce.";

    public static IServiceCollection AddOidcLogin(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OidcLoginOptions.Section);
        var options = section.Get<OidcLoginOptions>() ?? new OidcLoginOptions();
        services.Configure<OidcLoginOptions>(section);

        if (!options.IsConfigured)
        {
            return services;
        }

        var secureCookies = options.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var cookiePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        var authority = options.Authority.Trim().TrimEnd('/');

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ExternalScheme).Configure(cookie =>
        {
            cookie.Cookie.Name = ExternalCookieName;
            cookie.Cookie.SecurePolicy = cookiePolicy;
        });

        services.AddAuthentication().AddOpenIdConnect(OidcLoginOptions.Scheme, o =>
        {
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.Authority = authority;
            if (!string.IsNullOrWhiteSpace(options.BackchannelAuthority))
            {
                o.BackchannelHttpHandler = new OidcAuthorityRewriteHandler(authority, options.BackchannelAuthority, new HttpClientHandler());
            }

            o.ClientId = options.ClientId;
            o.ClientSecret = options.ClientSecret;
            o.RequireHttpsMetadata = options.RequireHttpsMetadata && !BackchannelIsPlainHttp(options);
            o.ResponseType = OpenIdConnectResponseType.Code;
            // Query, not form_post: sign-in stays a GET navigation, so the CSP's `form-action 'self'` needs no exception.
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.CallbackPath = OidcLoginOptions.CallbackPath;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("email");
            o.Scope.Add("profile");
            o.Scope.Add(OidcLoginOptions.RolesClaim);
            // The ID token is the end-session hint; the callback moves it into its own cookie and drops the rest with the external cookie.
            o.SaveTokens = true;
            o.MapInboundClaims = false;
            // Logto serves the email claims on userinfo, not in the ID token.
            o.GetClaimsFromUserInfoEndpoint = true;
            o.ClaimActions.MapUniqueJsonKey("email", "email");
            o.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
            // An array of role names; one claim per role. Kept only for the callback's app-role check.
            o.ClaimActions.MapJsonKey(OidcLoginOptions.RolesClaim, OidcLoginOptions.RolesClaim);
            // The issuer is pinned to the public address: a back-channel hop can never change who may sign a token.
            o.TokenValidationParameters.ValidIssuer = authority;

            // The code flow returns by GET navigation, so Lax cookies carry the correlation and nonce back.
            o.CorrelationCookie.Name = CorrelationCookieName;
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = cookiePolicy;
            o.NonceCookie.Name = NonceCookieName;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = cookiePolicy;

            o.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = options.RedirectUri;
                // The browser follows this redirect, so it must target the public address even when discovery ran on the back channel.
                if (!string.IsNullOrWhiteSpace(options.BackchannelAuthority) && context.ProtocolMessage.IssuerAddress is { Length: > 0 } issuerAddress)
                {
                    context.ProtocolMessage.IssuerAddress = OidcAuthorityRewriteHandler.ToPublic(issuerAddress, authority, options.BackchannelAuthority);
                }

                return Task.CompletedTask;
            };
            // Sign-out never redirects here: the endpoint reads the URL the handler built from the discovered end-session
            // endpoint and hands it to the SPA, which navigates the browser (a fetch cannot follow a cross-origin redirect).
            o.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                var message = context.ProtocolMessage;
                message.IdTokenHint = context.Properties.GetTokenValue(OpenIdConnectParameterNames.IdToken);
                message.ClientId = options.ClientId;
                message.PostLogoutRedirectUri = options.PostLogoutRedirectUri;
                if (!string.IsNullOrWhiteSpace(options.BackchannelAuthority) && message.IssuerAddress is { Length: > 0 } issuerAddress)
                {
                    message.IssuerAddress = OidcAuthorityRewriteHandler.ToPublic(issuerAddress, authority, options.BackchannelAuthority);
                }

                if (!string.IsNullOrWhiteSpace(message.IssuerAddress))
                {
                    context.HttpContext.Items[EndSessionUrlItem] = message.CreateLogoutRequestUrl();
                }

                context.HandleResponse();
                return Task.CompletedTask;
            };
            o.Events.OnAuthorizationCodeReceived = context =>
            {
                context.TokenEndpointRequest!.RedirectUri = options.RedirectUri;
                return Task.CompletedTask;
            };
            o.Events.OnRemoteFailure = context =>
            {
                context.Response.Redirect(LoginFailureUrl(options.PublicBaseUrl, FailureRedirectCode));
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });

        return services;
    }

    private static bool BackchannelIsPlainHttp(OidcLoginOptions options) =>
        !string.IsNullOrWhiteSpace(options.BackchannelAuthority)
        && !options.BackchannelAuthority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The SPA sign-in page, carrying an error code the page turns into a message.</summary>
    public static string LoginFailureUrl(string publicBaseUrl, string code) =>
        $"{publicBaseUrl.TrimEnd('/')}{OidcLoginOptions.SignInPath}?error={Uri.EscapeDataString(code)}";
}
