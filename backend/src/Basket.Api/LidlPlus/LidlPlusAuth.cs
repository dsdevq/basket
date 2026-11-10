using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Basket.Api.LidlPlus;

/// <summary>How Lidl answered an exchange of a refresh token.</summary>
public enum LidlAuthOutcome
{
    /// <summary>Lidl accepted the token and issued an access token.</summary>
    Accepted,

    /// <summary>Lidl refused the token: it is wrong, expired or already used. Asking again will not help; a new one is needed.</summary>
    Rejected,

    /// <summary>Lidl could not be asked, or answered in a way basket cannot read. The token may still be good.</summary>
    Unavailable,
}

/// <summary>
/// The result of one exchange. <see cref="RefreshToken"/> is the replacement Lidl issued, null when it kept the old one.
/// Both tokens are secrets: this type has no <c>ToString</c> that prints them, and nothing may log it.
/// </summary>
public sealed record LidlAuthResult(LidlAuthOutcome Outcome, string? AccessToken, string? RefreshToken, int ExpiresInSeconds)
{
    public static LidlAuthResult Rejected { get; } = new(LidlAuthOutcome.Rejected, null, null, 0);

    public static LidlAuthResult Unavailable { get; } = new(LidlAuthOutcome.Unavailable, null, null, 0);

    public override string ToString() => $"LidlAuthResult({Outcome})";
}

/// <summary>Lidl Plus sign-in. The one door to Lidl's account service, so tests can stand in for Lidl.</summary>
public interface ILidlPlusAuth
{
    Task<LidlAuthResult> RefreshAsync(string refreshToken, CancellationToken ct);
}

/// <summary>
/// Exchanges a refresh token at Lidl's <c>/connect/token</c> (OAuth2, <c>grant_type=refresh_token</c>, the app's client
/// credentials in a Basic header). The token travels only in the request body over HTTPS. Nothing here logs a body, a header or
/// an exception message that could hold one: every failure is reduced to an <see cref="LidlAuthOutcome"/>.
/// </summary>
public sealed class LidlPlusAuthClient(HttpClient http, IOptions<LidlPlusOptions> options) : ILidlPlusAuth
{
    public async Task<LidlAuthResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.ClientId}:{settings.ClientSecret}")));

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return LidlAuthResult.Rejected;
            }

            if (!response.IsSuccessStatusCode)
            {
                return LidlAuthResult.Unavailable;
            }

            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return LidlAuthResult.Unavailable;
        }
    }

    private static LidlAuthResult Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var access = Text(root, "access_token");
            if (access is null)
            {
                return LidlAuthResult.Unavailable;
            }

            var expires = root.TryGetProperty("expires_in", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var value)
                ? value
                : 0;
            return new LidlAuthResult(LidlAuthOutcome.Accepted, access, Text(root, "refresh_token"), expires);
        }
        catch (JsonException)
        {
            return LidlAuthResult.Unavailable;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
