namespace Basket.Api.Prices.Fetching;

public enum FetchStatus
{
    Ok,

    /// <summary>The site refused automated access (403, 429, an "Access Denied" page). Never retried or worked around.</summary>
    Blocked,

    NotFound,

    /// <summary>Timeout, server error, redirect off the site: probably temporary.</summary>
    Failed,
}

public sealed record FetchResult(FetchStatus Status, string? Body, string? Detail)
{
    public static FetchResult Ok(string body) => new(FetchStatus.Ok, body, null);
}

/// <summary>Reads one page of a shop's site, politely. The only way the price sources touch the network.</summary>
public interface IPageFetcher
{
    Task<FetchResult> GetAsync(Uri url, CancellationToken ct);
}
