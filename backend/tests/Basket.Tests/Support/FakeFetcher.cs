using Basket.Api.Prices.Fetching;

namespace Basket.Tests.Support;

/// <summary>A shop's site as recorded pages: what each address answers, and which addresses were asked for.</summary>
public sealed class FakeFetcher : IPageFetcher
{
    private readonly Dictionary<string, FetchResult> _pages = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public FakeFetcher Serve(string url, string body) => Serve(url, FetchResult.Ok(body));

    public FakeFetcher Serve(string url, FetchResult result)
    {
        _pages[url] = result;
        return this;
    }

    public Task<FetchResult> GetAsync(Uri url, CancellationToken ct)
    {
        Requested.Add(url.ToString());
        return Task.FromResult(_pages.GetValueOrDefault(url.ToString(), new FetchResult(FetchStatus.NotFound, null, "no such page")));
    }
}
