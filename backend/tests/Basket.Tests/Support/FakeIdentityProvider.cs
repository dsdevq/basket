using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Basket.Tests.Support;

/// <summary>Who the provider says is signing in: what its ID token and userinfo answer carry.</summary>
public sealed record ProviderPerson(string Subject, string? Email, bool EmailVerified, string? Name, params string[] Roles);

/// <summary>
/// Stands in for the org identity provider in tests: an in-process back channel that answers discovery, signing keys, the
/// token exchange (client secret and PKCE checked) and userinfo, signing ID tokens with a key generated for the run. Like
/// Logto it builds the endpoints it advertises from the host it was asked on (the back channel), while the issuer is the
/// public address. No real credential is involved anywhere.
/// </summary>
public sealed class FakeIdentityProvider : HttpMessageHandler
{
    public const string Authority = "https://identity.test/oidc";
    public const string Backchannel = "http://logto.test:3001/oidc";
    public const string ClientId = "test-basket";
    public const string ClientSecret = "test-client-secret";
    public const string KeyId = "idp-key";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ConcurrentDictionary<string, Grant> _codes = new();
    private readonly ConcurrentDictionary<string, ProviderPerson> _accessTokens = new();

    /// <summary>Every URL the app asked this provider for, in order.</summary>
    public ConcurrentQueue<Uri> Requests { get; } = new();

    /// <summary>The ID token the provider issued last, so a test can tell it came back as the sign-out hint.</summary>
    public string? LastIdToken { get; private set; }

    private sealed record Grant(ProviderPerson Person, string Nonce, string CodeChallenge, string Issuer);

    /// <summary>The one-time code a browser would come back with after signing in at the provider.</summary>
    public string IssueCode(ProviderPerson person, string nonce, string codeChallenge, string issuer = Authority)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new Grant(person, nonce, codeChallenge, issuer);
        return code;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        HandleAsync(request, cancellationToken);

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        var path = uri.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == "/oidc/.well-known/openid-configuration")
        {
            return Json(new Dictionary<string, object>
            {
                ["issuer"] = Authority,
                ["authorization_endpoint"] = Backchannel + "/auth",
                ["token_endpoint"] = Backchannel + "/token",
                ["userinfo_endpoint"] = Backchannel + "/me",
                ["jwks_uri"] = Backchannel + "/jwks",
                ["end_session_endpoint"] = Backchannel + "/session/end",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
            });
        }

        if (request.Method == HttpMethod.Get && path == "/oidc/jwks")
        {
            var p = _key.ExportParameters(false);
            return Json(new Dictionary<string, object>
            {
                ["keys"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["kty"] = "EC",
                        ["crv"] = "P-256",
                        ["x"] = Base64UrlEncoder.Encode(p.Q.X!),
                        ["y"] = Base64UrlEncoder.Encode(p.Q.Y!),
                        ["kid"] = KeyId,
                        ["use"] = "sig",
                        ["alg"] = "ES256",
                    },
                },
            });
        }

        if (request.Method == HttpMethod.Post && path == "/oidc/token")
        {
            return Exchange(await FormAsync(request, cancellationToken), request.Headers.Authorization);
        }

        if (request.Method == HttpMethod.Get && path == "/oidc/me")
        {
            var token = request.Headers.Authorization?.Parameter;
            if (token is null || !_accessTokens.TryGetValue(token, out var person))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var claims = new Dictionary<string, object> { ["sub"] = person.Subject, ["roles"] = person.Roles };
            if (person.Email is not null)
            {
                claims["email"] = person.Email;
                claims["email_verified"] = person.EmailVerified;
            }

            if (person.Name is not null)
            {
                claims["name"] = person.Name;
            }

            return Json(claims);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Exchange(Dictionary<string, string> form, AuthenticationHeaderValue? basic)
    {
        var secret = form.GetValueOrDefault("client_secret");
        if (basic is { Scheme: "Basic", Parameter: { } encoded })
        {
            secret = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split(':', 2)[1];
        }

        if (form.GetValueOrDefault("grant_type") != "authorization_code"
            || form.GetValueOrDefault("redirect_uri") != BasketFactory.Origin + "/api/auth/oidc/signin"
            || secret != ClientSecret
            || !_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var grant)
            || Pkce(form.GetValueOrDefault("code_verifier")) != grant.CodeChallenge)
        {
            return Json(new Dictionary<string, object> { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
        }

        var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _accessTokens[accessToken] = grant.Person;
        var now = DateTime.UtcNow;
        LastIdToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = grant.Issuer,
            Audience = ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = grant.Person.Subject, ["nonce"] = grant.Nonce },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = KeyId }, SecurityAlgorithms.EcdsaSha256),
        });

        return Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 600,
            ["id_token"] = LastIdToken,
        });
    }

    /// <summary>The S256 challenge a client derives from its code verifier.</summary>
    private static string Pkce(string? verifier) =>
        verifier is null ? string.Empty : Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static async Task<Dictionary<string, string>> FormAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Dispose();
        }

        base.Dispose(disposing);
    }
}
