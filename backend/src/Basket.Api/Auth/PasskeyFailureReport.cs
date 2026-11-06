namespace Basket.Api.Auth;

/// <summary>
/// What the join page says about a passkey attempt that failed in the browser. Every field is a short label
/// from a fixed list: no token, no name, no credential, no free text. Anything else is logged as "other".
/// </summary>
public sealed record PasskeyFailureRequest(string? Kind, string? ErrorName, string? Stage, int? ElapsedMs, int? HttpStatus, bool? PlatformAuthenticator);

/// <summary>
/// A failure that happens inside the browser never reaches the server by itself, so the join page reports it
/// here. The line it writes is what makes the next failure diagnosable from the log alone (the request log
/// line beside it carries the user agent).
/// </summary>
public static class PasskeyFailureReport
{
    internal static readonly HashSet<string> Kinds =
        ["no-api", "insecure-context", "not-supported", "security", "blocked", "cancelled", "http", "other"];

    internal static readonly HashSet<string> ErrorNames =
        ["NotAllowedError", "AbortError", "SecurityError", "NotSupportedError", "InvalidStateError", "ConstraintError",
         "DataError", "TypeError", "UnknownError", "NetworkError", "PasskeyClientError"];

    internal static readonly HashSet<string> Stages = ["page", "options", "create", "register"];

    /// <summary>Longer than any real prompt (the options time out at 5 minutes), so a wild number is clamped rather than trusted.</summary>
    public const int MaxElapsedMs = 600_000;

    public static IResult Handle(PasskeyFailureRequest request, ILogger<PasskeyFailureRequest> log)
    {
        var report = Clean(request);
        log.LogWarning(
            "Join passkey failed: kind={Kind} error={ErrorName} stage={Stage} elapsedMs={ElapsedMs} http={HttpStatus} platformAuthenticator={PlatformAuthenticator}",
            report.Kind, report.ErrorName, report.Stage, report.ElapsedMs, report.HttpStatus, report.PlatformAuthenticator);
        return Results.NoContent();
    }

    public static (string Kind, string ErrorName, string Stage, int? ElapsedMs, int? HttpStatus, bool? PlatformAuthenticator) Clean(PasskeyFailureRequest request) => (
        Pick(request.Kind, Kinds),
        Pick(request.ErrorName, ErrorNames),
        Pick(request.Stage, Stages),
        request.ElapsedMs is { } ms ? Math.Clamp(ms, 0, MaxElapsedMs) : null,
        request.HttpStatus is { } status and >= 100 and <= 599 ? status : null,
        request.PlatformAuthenticator);

    private static string Pick(string? value, HashSet<string> allowed) =>
        value is not null && allowed.Contains(value) ? value : "other";
}
