using Basket.Api.Auth;
using Microsoft.Extensions.Options;

namespace Basket.Api.Infrastructure;

/// <summary>
/// CSRF defence for the cookie-authenticated API, on top of the SameSite=Strict session cookie: a state-changing
/// request must name an allowed origin. Browsers always send Origin on such requests, so a missing one is refused.
/// </summary>
public sealed class OriginCheckMiddleware(RequestDelegate next, IOptions<AuthOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var unsafeMethod = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method);
        if (unsafeMethod && request.Path.StartsWithSegments("/api")
            && !options.Value.IsAllowedOrigin(request.Headers.Origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}

/// <summary>Response headers for a public surface. The CSP is configuration so the theme script's hash can be pinned in it.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] = options.Value.ContentSecurityPolicy;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Permissions-Policy"] =
                "camera=(), microphone=(), geolocation=(), payment=(), usb=(), publickey-credentials-get=(self), publickey-credentials-create=(self)";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                h.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }
}

public sealed class SecurityOptions
{
    public const string Section = "Security";

    public string ContentSecurityPolicy { get; set; } = "default-src 'self'";
}
