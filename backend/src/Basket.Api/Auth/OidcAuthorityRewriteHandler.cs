namespace Basket.Api.Auth;

/// <summary>
/// Sends the OIDC handler's server-side calls (discovery, signing keys, token, userinfo) to a back-channel address while
/// the issuer it validates, and the browser's authorize redirect, stay on the public authority. Any request URL under the
/// public authority is re-based onto the back-channel authority; everything else passes through. The provider builds its
/// discovery endpoints from the host it was asked on, so the browser-facing ones it returns on the back channel are mapped
/// back with <see cref="ToPublic"/>.
/// </summary>
public sealed class OidcAuthorityRewriteHandler(string publicAuthority, string backchannelAuthority, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    private readonly string _publicPrefix = publicAuthority.TrimEnd('/');
    private readonly string _backchannelPrefix = backchannelAuthority.TrimEnd('/');

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri;
        if (url is not null && IsUnder(url, _publicPrefix))
        {
            request.RequestUri = new Uri(_backchannelPrefix + url[_publicPrefix.Length..]);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Maps a browser-facing endpoint the provider advertised on the back channel back onto the public authority.</summary>
    public static string ToPublic(string url, string publicAuthority, string backchannelAuthority)
    {
        var from = backchannelAuthority.TrimEnd('/');
        return IsUnder(url, from) ? publicAuthority.TrimEnd('/') + url[from.Length..] : url;
    }

    private static bool IsUnder(string url, string prefix) =>
        url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (url.Length == prefix.Length || url[prefix.Length] is '/' or '?');
}
