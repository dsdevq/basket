namespace Basket.Api.Infrastructure;

/// <summary>
/// The platform contract's observability pieces (lifekit-stack <c>docs/platform-contract.md</c>): metrics as Prometheus
/// text on <see cref="MetricsPath"/>, scraped by the box Prometheus over the external <c>lifekit-shared</c> network.
/// </summary>
public static class Observability
{
    public const string MetricsPath = "/metrics";

    /// <summary>
    /// The scrape is for Prometheus on the docker network, not for a person: Tailscale (Serve or Funnel) always
    /// proxies with <c>X-Forwarded-For</c>, Prometheus never does. A request that came through the proxy is answered
    /// 404, so the runtime numbers are not served on the public address. It must run before the forwarded-headers
    /// middleware, which consumes the header.
    /// </summary>
    public static bool CameThroughProxy(HttpRequest request) => request.Headers.ContainsKey("X-Forwarded-For");

    /// <summary>
    /// Whether the request-log line for this request is written. Health probes are polled every few seconds by the
    /// container healthcheck and stay out of the log, except a probe that carries a W3C <c>traceparent</c>: a caller
    /// that sends trace context (the contract checker) gets the line, with that trace id, so a quiet app still has a
    /// traced line to show.
    /// </summary>
    public static bool IsLoggedHealthProbe(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/health") && request.Headers.ContainsKey("traceparent");
}
