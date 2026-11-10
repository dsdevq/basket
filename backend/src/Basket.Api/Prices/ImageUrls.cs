namespace Basket.Api.Prices;

/// <summary>
/// The one check on a product photo's address before it is stored: an absolute https address of bounded length. basket
/// keeps only the address, never the picture, and the shelf's browser loads it from the shop's own image host.
/// </summary>
public static class ImageUrls
{
    public const int MaxLength = 500;

    /// <summary>The address trimmed and as the shop wrote it, or false when it is absent, too long, not https, or carries a login.</summary>
    public static bool TryNormalize(string? raw, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength
            || trimmed.Any(char.IsControl)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || parsed.UserInfo.Length > 0
            || parsed.Host.Length == 0)
        {
            return false;
        }

        url = trimmed;
        return true;
    }
}
