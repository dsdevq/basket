using System.ComponentModel.DataAnnotations;

namespace Basket.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Origins the app is served from, scheme + host + port, the first being the public one that invite links use.
    /// Passkey ceremonies and every state-changing request must come from one of them.
    /// </summary>
    [Required, MinLength(1)]
    public string[] Origins { get; set; } = [];

    /// <summary>The WebAuthn relying-party id. Defaults to the host of the first origin.</summary>
    public string? ServerDomain { get; set; }

    public string CookieName { get; set; } = "__Host-basket";

    public int SessionDays { get; set; } = 30;

    /// <summary>Requests per minute per client address on the sign-in, invite and registration endpoints.</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;

    /// <summary>Requests per minute per client address on the price ingest endpoint (one batch a night is the norm).</summary>
    public int IngestRequestsPerMinute { get; set; } = 30;

    /// <summary>Invites an owner can mint per hour.</summary>
    public int InvitesPerHour { get; set; } = 10;

    /// <summary>
    /// First-run only: while no user exists, a registration link made from this token is valid for 24 hours
    /// from each start. It lives in the deploy env file, never in git, and is ignored once an owner exists.
    /// </summary>
    public string? BootstrapToken { get; set; }

    public string PublicOrigin => Origins[0].TrimEnd('/');

    public string RelyingPartyId => ServerDomain ?? new Uri(PublicOrigin).Host;

    public bool IsAllowedOrigin(string? origin) =>
        !string.IsNullOrEmpty(origin)
        && Origins.Any(o => string.Equals(o.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
