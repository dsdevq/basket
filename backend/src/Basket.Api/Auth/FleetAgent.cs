using System.Security.Claims;
using Basket.Api.Prices.Ingest;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Basket.Api.Auth;

/// <summary>
/// The fleet's own machine identity (the <c>fleet-agent</c> application in the owner's Logto), bound from
/// <c>FleetAgent</c>. People never use it: sign-in stays passkey-only. The whole path is off until
/// <see cref="Authority"/> is set, and then it only opens the machine endpoints named in <see cref="FleetScopes"/>.
/// </summary>
public sealed class FleetAgentOptions
{
    public const string Section = "FleetAgent";

    /// <summary>The issuer the tokens carry, e.g. <c>https://host.tailnet.ts.net:3001/oidc</c>. Empty = the path is off.</summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Optional address basket itself uses to fetch the issuer's discovery document and signing keys, e.g.
    /// <c>http://logto:3001/oidc</c> on a shared Docker network. The issuer validated stays <see cref="Authority"/>.
    /// </summary>
    public string? BackchannelAuthority { get; set; }

    /// <summary>The API resource the tokens are issued for. It is only an audience name; nothing resolves it.</summary>
    public string Audience { get; set; } = "https://fleet.lifekit.internal/api";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);

    public string Issuer => Authority?.Trim().TrimEnd('/') ?? string.Empty;
}

/// <summary>One scope per action class the fleet identity may take on basket; the Logto role holds exactly these.</summary>
public static class FleetScopes
{
    public const string PriceIngest = "basket:price-ingest";
    public const string CatalogRead = "basket:catalog-read";
    public const string CatalogAdd = "basket:catalog-add";
}

/// <summary>A caller that presented a valid fleet token: who it was and which scopes the token carried.</summary>
public sealed record FleetCaller(string ClientId, IReadOnlySet<string> Scopes)
{
    public bool Has(string scope) => Scopes.Contains(scope);

    /// <summary>The fleet identity in <paramref name="principal"/>, or null when it holds none.</summary>
    public static FleetCaller? From(ClaimsPrincipal? principal)
    {
        var identity = principal?.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == FleetAgent.Scheme);
        if (identity is null)
        {
            return null;
        }

        var scopes = identity.FindAll(FleetAgent.ScopeClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        return new FleetCaller(identity.FindFirst(FleetAgent.ClientIdClaim)?.Value ?? "unknown", scopes);
    }
}

public static class FleetAgent
{
    public const string Scheme = "FleetAgent";
    public const string ScopeClaim = "scope";
    public const string ClientIdClaim = "client_id";

    private static readonly string[] Algorithms =
        ["ES256", "ES384", "ES512", "RS256", "RS384", "RS512", "PS256", "PS384", "PS512"];

    /// <summary>True when the request carries an <c>Authorization: Bearer</c> header, whatever is in it.</summary>
    public static bool HasBearer(HttpRequest request) => BearerToken(request) is not null;

    /// <summary>The raw bearer value, or null when there is none.</summary>
    public static string? BearerToken(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Registers the JWT scheme that validates fleet tokens. It is not the default scheme: nothing authenticates
    /// with it unless an endpoint's policy lists it or the ingest endpoint asks for it by name.
    /// </summary>
    public static IServiceCollection AddFleetAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FleetAgentOptions>().Bind(configuration.GetSection(FleetAgentOptions.Section));

        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(Scheme).Configure<IOptions<FleetAgentOptions>>((jwt, fleet) => Configure(jwt, fleet.Value));

        services.AddScoped<FleetAgentAuthenticator>();
        services.AddSingleton<IAuthorizationHandler, MemberOrFleetScopeHandler>();
        return services;
    }

    private static void Configure(JwtBearerOptions jwt, FleetAgentOptions fleet)
    {
        // Claim names stay as the token has them ("scope", "client_id"); nothing is remapped to Microsoft's types.
        jwt.MapInboundClaims = false;
        jwt.SaveToken = false;
        jwt.IncludeErrorDetails = false;

        if (fleet.IsConfigured)
        {
            var metadata = string.IsNullOrWhiteSpace(fleet.BackchannelAuthority)
                ? fleet.Issuer
                : fleet.BackchannelAuthority.Trim().TrimEnd('/');
            jwt.MetadataAddress = metadata + "/.well-known/openid-configuration";
            jwt.RequireHttpsMetadata = metadata.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // No keys at all: with the path off, no token can validate.
            jwt.Configuration = new OpenIdConnectConfiguration();
        }

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = fleet.IsConfigured ? fleet.Issuer : "fleet-agent-not-configured",
            ValidateAudience = true,
            ValidAudience = fleet.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = Algorithms,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        jwt.Events = new JwtBearerEvents { OnTokenValidated = OnTokenValidated };
    }

    /// <summary>
    /// Replaces the token's principal with one that holds only what basket uses: the client id and the scopes, under
    /// the <see cref="Scheme"/> authentication type. It has no user id, role or name, so nothing written for a person
    /// can mistake it for one.
    /// </summary>
    private static Task OnTokenValidated(TokenValidatedContext context)
    {
        var source = context.Principal;
        var clientId = source?.FindFirst(ClientIdClaim)?.Value ?? source?.FindFirst("sub")?.Value;
        var scopes = (source?.FindFirst(ScopeClaim)?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var identity = new ClaimsIdentity(Scheme);
        identity.AddClaim(new Claim(ClientIdClaim, clientId ?? "unknown"));
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            identity.AddClaim(new Claim(ScopeClaim, scope));
        }

        context.Principal = new ClaimsPrincipal(identity);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Validates a fleet token on an endpoint that does its own authorization (the ingest endpoint, which also takes the
/// owner-minted <c>bsk_</c> token). Never throws and says nothing about why a token failed.
/// </summary>
public sealed class FleetAgentAuthenticator(IOptions<FleetAgentOptions> options)
{
    public async Task<FleetCaller?> AuthenticateAsync(HttpContext http)
    {
        if (!options.Value.IsConfigured)
        {
            return null;
        }

        var token = FleetAgent.BearerToken(http.Request);
        if (token is null || token.StartsWith(IngestTokens.Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var result = await http.AuthenticateAsync(FleetAgent.Scheme);
        return result.Succeeded ? FleetCaller.From(result.Principal) : null;
    }
}

/// <summary>Needs the fleet scope. Declared by the endpoint, evaluated on the principal of the schemes the policy lists.</summary>
public sealed class FleetScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

/// <summary>
/// The catalog endpoints take a signed-in person (as before) or a fleet token holding the endpoint's scope. A request
/// that carries a bearer header is judged on the fleet token alone: the session cookie is ignored, so a cookie can never
/// carry a request that the fleet token would not.
/// </summary>
public sealed class MemberOrFleetScopeHandler(ILogger<MemberOrFleetScopeHandler> log) : AuthorizationHandler<FleetScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FleetScopeRequirement requirement)
    {
        if (context.Resource is HttpContext http && FleetAgent.HasBearer(http.Request))
        {
            var caller = FleetCaller.From(context.User);
            if (caller is not null && caller.Has(requirement.Scope))
            {
                log.LogInformation("Fleet agent {ClientId} authorised for {Scope}", caller.ClientId, requirement.Scope);
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (context.User.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == IdentityConstants.ApplicationScheme))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
