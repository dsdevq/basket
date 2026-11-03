using Serilog.AspNetCore;

namespace Basket.Api.Infrastructure;

/// <summary>
/// The per-request log line. It names the client's browser (User-Agent, the Sec-CH-UA hint, Accept-Language) so
/// a visit that never reaches the API can be told apart from one that fails: those headers are all the server
/// ever learns about an unusual browser. Only the path is logged, never the query string, and the URL fragment
/// (where the invite token lives) never leaves the browser.
/// </summary>
public static class RequestLogging
{
    public const string MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms ua={UserAgent} ch-ua={SecChUa} lang={AcceptLanguage}";

    private const int MaxHeaderLength = 256;

    public static void Configure(RequestLoggingOptions options)
    {
        options.MessageTemplate = MessageTemplate;
        options.EnrichDiagnosticContext = (diagnostics, context) =>
        {
            var headers = context.Request.Headers;
            diagnostics.Set("UserAgent", Clean(headers.UserAgent.ToString()));
            diagnostics.Set("SecChUa", Clean(headers["Sec-CH-UA"].ToString()));
            diagnostics.Set("AcceptLanguage", Clean(headers.AcceptLanguage.ToString()));
        };
    }

    /// <summary>A header value safe for one log line: control characters dropped, length capped, "-" when absent.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var span = value.AsSpan().Trim();
        if (span.Length > MaxHeaderLength)
        {
            span = span[..MaxHeaderLength];
        }

        return string.Create(span.Length, span.ToString(), static (buffer, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                buffer[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }
}
