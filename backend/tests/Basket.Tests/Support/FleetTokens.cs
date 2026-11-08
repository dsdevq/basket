using System.Security.Cryptography;
using Basket.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Basket.Tests.Support;

/// <summary>
/// Stands in for the identity service in tests: a throwaway signing key generated per run, and tokens shaped like the
/// ones Logto issues to a machine-to-machine application. No real credential is involved anywhere.
/// </summary>
public sealed class FleetTokens : IDisposable
{
    public const string Issuer = "https://identity.test/oidc";
    public const string Audience = "https://fleet.lifekit.internal/api";
    public const string ClientId = "test-fleet-agent";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

    public SecurityKey PublicKey => new ECDsaSecurityKey(ECDsa.Create(_key.ExportParameters(false))) { KeyId = "test-key" };

    public string Mint(
        string? scope,
        string issuer = Issuer,
        string audience = Audience,
        TimeSpan? expiresIn = null,
        SecurityKey? signWith = null,
        string clientId = ClientId)
    {
        var now = DateTime.UtcNow;
        var lifetime = expiresIn ?? TimeSpan.FromHours(1);
        var claims = new Dictionary<string, object> { ["sub"] = clientId, [FleetAgent.ClientIdClaim] = clientId };
        if (scope is not null)
        {
            claims[FleetAgent.ScopeClaim] = scope;
        }

        var key = signWith ?? new ECDsaSecurityKey(_key) { KeyId = "test-key" };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = lifetime < TimeSpan.Zero ? now - TimeSpan.FromHours(2) : now,
            NotBefore = lifetime < TimeSpan.Zero ? now - TimeSpan.FromHours(2) : now,
            Expires = now + lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha384),
        });
    }

    /// <summary>A token signed by a different key than the one the app trusts.</summary>
    public string MintForged(string scope)
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        return Mint(scope, signWith: new ECDsaSecurityKey(other) { KeyId = "test-key" });
    }

    public void Dispose() => _key.Dispose();
}
