using System.IO.Compression;
using System.Net;
using System.Text;

namespace Basket.Api.Prices.Fetching;

/// <summary>
/// An ordinary GET with an honest user agent, one request at a time per site with a pause in between. It does not
/// retry, does not rotate anything and does not look for a way round a refusal: a 403 or 429 is reported as blocked.
/// </summary>
public sealed class PoliteFetcher(
    HttpClient http,
    HostThrottle throttle,
    ILogger<PoliteFetcher> log) : IPageFetcher
{
    public async Task<FetchResult> GetAsync(Uri url, CancellationToken ct)
    {
        await throttle.WaitAsync(url.Host, ct);
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.RequestMessage?.RequestUri?.Host is { } finalHost && !string.Equals(finalHost, url.Host, StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult(FetchStatus.Failed, null, $"{url.Host} redirected to another site");
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized:
                    log.LogWarning("{Host} refused automated access with HTTP {Status}", url.Host, (int)response.StatusCode);
                    return new FetchResult(FetchStatus.Blocked, null, $"{url.Host} answered HTTP {(int)response.StatusCode}");
                case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    return new FetchResult(FetchStatus.NotFound, null, $"{url.Host} answered HTTP {(int)response.StatusCode}");
                case not HttpStatusCode.OK:
                    return new FetchResult(FetchStatus.Failed, null, $"{url.Host} answered HTTP {(int)response.StatusCode}");
            }

            var body = url.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? await ReadGzipAsync(response.Content, ct)
                : await response.Content.ReadAsStringAsync(ct);
            return LooksLikeBlockPage(body)
                ? new FetchResult(FetchStatus.Blocked, null, $"{url.Host} sent an access-denied page")
                : FetchResult.Ok(body);
        }
        catch (InvalidDataException)
        {
            return new FetchResult(FetchStatus.Failed, null, $"{url.Host} sent a file that could not be unpacked");
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult(FetchStatus.Failed, null, $"{url.Host} could not be reached ({ex.HttpRequestError})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(FetchStatus.Failed, null, $"{url.Host} did not answer in time");
        }
    }

    /// <summary>The largest sitemap, unpacked, that is read (the shops' are a few MB at most).</summary>
    private const int MaxUnpackedBytes = 20 * 1024 * 1024;

    /// <summary>A <c>.xml.gz</c> sitemap is a file, not a transfer encoding, so the HTTP client does not unpack it.</summary>
    private static async Task<string> ReadGzipAsync(HttpContent content, CancellationToken ct)
    {
        await using var packed = await content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await gzip.ReadAsync(buffer, ct)) > 0)
        {
            unpacked.Write(buffer, 0, read);
            if (unpacked.Length > MaxUnpackedBytes)
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "The sitemap is larger than expected.");
            }
        }

        return Encoding.UTF8.GetString(unpacked.GetBuffer(), 0, (int)unpacked.Length);
    }

    /// <summary>The error page the shops' bot protection serves instead of the content.</summary>
    public static bool LooksLikeBlockPage(string body) =>
        body.Length < 4_000 && body.Contains("<TITLE>Access Denied</TITLE>", StringComparison.OrdinalIgnoreCase);
}
