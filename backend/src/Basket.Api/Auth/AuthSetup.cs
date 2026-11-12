using System.Security.Claims;
using System.Threading.RateLimiting;
using Basket.Api.Data;
using Basket.Api.Prices.Ingest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Basket.Api.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddBasketAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddIdentityCore<AppUser>(options =>
            {
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager();
        services.AddScoped<IUserClaimsPrincipalFactory<AppUser>, BasketClaimsPrincipalFactory>();

        services.AddOptions<IdentityPasskeyOptions>().Configure<IOptions<AuthOptions>>((passkeys, auth) =>
        {
            passkeys.ServerDomain = auth.Value.RelyingPartyId;
            passkeys.AuthenticatorTimeout = TimeSpan.FromMinutes(3);
            // Not just the host: the whole origin, so another service on the same machine cannot use a basket passkey.
            passkeys.ValidateOrigin = context => ValueTask.FromResult(auth.Value.IsAllowedOrigin(context.Origin));
        });

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<AuthOptions>>((cookie, auth) =>
            {
                var days = TimeSpan.FromDays(auth.Value.SessionDays);
                cookie.Cookie.Name = auth.Value.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.Path = "/";
                cookie.Cookie.MaxAge = days;
                cookie.ExpireTimeSpan = days;
                cookie.SlidingExpiration = true;
                // An API, not pages: answer with a status code, never a redirect to a login page.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddOidcLogin(configuration);
        services.AddScoped<ExternalLoginService>();
        services.AddFleetAgent(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Owner, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Owner))
            .AddPolicy(Policies.CatalogRead, policy => FleetOrMember(policy, FleetScopes.CatalogRead))
            .AddPolicy(Policies.CatalogAdd, policy => FleetOrMember(policy, FleetScopes.CatalogAdd));

        services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.AddPolicy(AuthEndpoints.AuthLimiter, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.AuthRequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            limits.AddPolicy(IngestEndpoints.Limiter, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.IngestRequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            limits.AddPolicy(AuthEndpoints.InviteLimiter, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.InvitesPerHour;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
            });
        });

        return services;
    }

    /// <summary>
    /// A machine endpoint: a signed-in person as before, or the fleet identity holding <paramref name="scope"/>. Both
    /// schemes are listed so either can authenticate; the requirement decides which one counts.
    /// </summary>
    private static void FleetOrMember(AuthorizationPolicyBuilder policy, string scope) => policy
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, FleetAgent.Scheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new FleetScopeRequirement(scope));

    /// <summary>The caller's address after the forwarded-headers middleware has run.</summary>
    private static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Adds the display name to the principal, so the session endpoint needs no database read.</summary>
public sealed class BasketClaimsPrincipalFactory(
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(Policies.DisplayNameClaim, user.DisplayName));
        return identity;
    }
}
