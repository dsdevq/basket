using Basket.Api.Data;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Basket.Api.Prices.Catalog;

/// <summary>
/// The nightly Lidl catalog sweep. lidl.ie lists its whole range but shows an everyday price on only part of it, so this is a
/// range reader: the food listing pages found in the page sitemap are walked with <c>?offset=</c> in steps of 12, each tile is
/// stored as a catalog product through <see cref="CatalogRecorder"/> (the path the PC reader's endpoint also ends in). A sweep that
/// read every category to its end is closed through <see cref="CatalogIngest.CompleteAsync"/>, so the discontinued guard and the
/// run row are the ones every shop has; a sweep that missed any page is recorded as it was and delists nothing. Then the product
/// pages of at most <see cref="PricesOptions.LidlBarcodePagesPerNight"/> products with no stored barcode are opened to read the
/// GTIN. It never calls Lidl's search API, and a refusal ends the sweep (nothing is tried again or worked around).
/// </summary>
public sealed class LidlCatalogSweep(
    IPageFetcher fetcher,
    AppDbContext db,
    CatalogRecorder recorder,
    CatalogIngest ingest,
    IOptions<PricesOptions> options,
    TimeProvider clock,
    ILogger<LidlCatalogSweep> log)
{
    /// <summary>Tiles per listing page; Lidl's server decides it, the sweep only steps by it.</summary>
    public const int PageSize = 12;

    /// <summary>A category is never walked past this many pages (the food range is about 60), so a page that repeats cannot loop.</summary>
    private const int MaxPagesPerCategory = 120;

    /// <summary>The sitemap index of lidl.ie (robots.txt names it); the page sitemap it lists holds the category pages.</summary>
    private static readonly Uri SitemapIndex = new("https://www.lidl.ie/static/sitemap.xml");

    public async Task RunAsync(CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var readings = new Dictionary<string, CatalogReading>(StringComparer.Ordinal);

        var discovery = await DiscoverCategoriesAsync(ct);
        var refused = discovery.Refused;
        var note = discovery.Note;

        var rejected = 0;
        foreach (var category in discovery.Pages)
        {
            if (refused)
            {
                break;
            }

            var walked = false;
            for (var page = 0; page < MaxPagesPerCategory; page++)
            {
                var result = await fetcher.GetAsync(page == 0 ? category : new Uri($"{category}?offset={page * PageSize}"), ct);
                if (result.Status == FetchStatus.Blocked)
                {
                    refused = true;
                    note = $"{result.Detail}. basket does not try to get around a block.";
                    break;
                }

                if (result.Status != FetchStatus.Ok)
                {
                    note ??= $"A Lidl listing page was not read ({result.Detail ?? "no detail"}).";
                    break;
                }

                var tiles = LidlParser.ParseCatalogTiles(result.Body!);
                if (tiles.Count == 0)
                {
                    walked = page > 0; // an empty first page is a layout that changed or a page that failed, not a category with nothing in it
                    if (!walked)
                    {
                        note ??= $"A Lidl listing page showed no products ({category.AbsolutePath}).";
                    }

                    break;
                }

                var fresh = new List<CatalogReading>();
                var repeated = 0;
                foreach (var item in tiles)
                {
                    if (item.ExternalId is { } id && readings.ContainsKey(id))
                    {
                        repeated++;
                    }
                    else if (CatalogReading.TryCreate(Store.Lidl, item, out var reading, out _))
                    {
                        readings[reading!.ExternalId] = reading;
                        fresh.Add(reading);
                    }
                    else
                    {
                        rejected++;
                    }
                }

                if (fresh.Count == 0)
                {
                    walked = repeated > 0; // past the last page: it only repeats what was seen; a page of unusable tiles is a failed read
                    if (!walked)
                    {
                        note ??= $"A Lidl listing page showed no usable products ({category.AbsolutePath}).";
                    }

                    break;
                }

                await recorder.RecordAsync(Store.Lidl, fresh, clock.GetUtcNow(), ct);
                await db.SaveChangesAsync(ct);
            }

            if (!walked && !refused)
            {
                note ??= $"A Lidl category still listed new products after {MaxPagesPerCategory} pages ({category.AbsolutePath}).";
            }
        }

        if (rejected > 0)
        {
            log.LogWarning("Lidl catalog sweep: {Rejected} tiles were unusable", rejected);
        }

        if (refused)
        {
            await RecordRunAsync(started, PullOutcome.Blocked, readings.Count, note ?? "Lidl refused automated access.", ct);
            return;
        }

        if (note is not null)
        {
            log.LogWarning("Lidl catalog sweep incomplete, nothing marked discontinued: {Note}", note);
            await RecordRunAsync(
                started, readings.Count == 0 ? PullOutcome.Failed : PullOutcome.Partial, readings.Count, $"{note} Nothing was marked discontinued.", ct);
        }
        else
        {
            var closed = await ingest.CompleteAsync(Store.Lidl, readings.Count, started, ct);
            log.LogInformation("Lidl catalog sweep: {Seen} products, {Outcome}", closed.Seen, closed.Outcome);
        }

        await ReadBarcodesAsync(readings, ct);
    }

    private sealed record Discovery(IReadOnlyList<Uri> Pages, bool Refused, string? Note);

    /// <summary>
    /// The food category pages of the page sitemap: the index names the sitemaps, the one named "pages" lists the categories.
    /// A sitemap that cannot be read leaves no categories to walk and says so; a refusal ends the sweep.
    /// </summary>
    private async Task<Discovery> DiscoverCategoriesAsync(CancellationToken ct)
    {
        var index = await fetcher.GetAsync(SitemapIndex, ct);
        if (index.Status != FetchStatus.Ok)
        {
            return Unreadable(index, "sitemap index");
        }

        var pagesSitemap = LidlParser.SitemapLocations(index.Body!)
            .FirstOrDefault(u => Path.GetFileName(u.AbsolutePath).StartsWith("pages", StringComparison.OrdinalIgnoreCase));
        if (pagesSitemap is null)
        {
            return new Discovery([], false, "The Lidl sitemap index lists no page sitemap.");
        }

        var sitemap = await fetcher.GetAsync(pagesSitemap, ct);
        if (sitemap.Status != FetchStatus.Ok)
        {
            return Unreadable(sitemap, "page sitemap");
        }

        var pages = LidlParser.FoodCategoryPages(sitemap.Body!);
        return new Discovery(pages, false, pages.Count == 0 ? "The Lidl page sitemap lists no food categories." : null);
    }

    private static Discovery Unreadable(FetchResult result, string what) =>
        result.Status == FetchStatus.Blocked
            ? new Discovery([], true, $"{result.Detail}. basket does not try to get around a block.")
            : new Discovery([], false, $"The Lidl {what} was not read ({result.Detail ?? "no detail"}).");

    /// <summary>
    /// Opens the product page of products with no stored barcode (at most the nightly cap) and stores the GTIN of the JSON-LD.
    /// The candidates are in product-number order, started from a place that moves on by the cap every UTC day, so products whose
    /// page never gives a valid GTIN cannot take the whole cap every night. A page that is gone, down or without a valid GTIN is
    /// skipped and tried again on a later night; a refusal ends it.
    /// </summary>
    private async Task ReadBarcodesAsync(Dictionary<string, CatalogReading> readings, CancellationToken ct)
    {
        var cap = Math.Max(options.Value.LidlBarcodePagesPerNight, 0);
        if (cap == 0 || readings.Count == 0)
        {
            return;
        }

        var ids = readings.Keys.ToList();
        var candidates = await db.StoreProducts
            .Where(p => p.Store == Store.Lidl && p.Barcode == null && ids.Contains(p.ExternalId))
            .OrderBy(p => p.ExternalId)
            .Select(p => p.ExternalId)
            .ToListAsync(ct);
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber;
        var first = candidates.Count == 0 ? 0 : (int)(day * (long)cap % candidates.Count);
        var wanted = candidates.Skip(first).Concat(candidates.Take(first)).Take(cap).ToList();

        var found = 0;
        foreach (var id in wanted)
        {
            var reading = readings[id];
            var page = await fetcher.GetAsync(new Uri(reading.Url), ct);
            if (page.Status == FetchStatus.Blocked)
            {
                log.LogWarning("Lidl refused a product page while the barcodes were read: {Detail}", page.Detail);
                break;
            }

            if (page.Status != FetchStatus.Ok || LidlParser.ParseGtin(page.Body!) is not { } gtin)
            {
                continue;
            }

            await recorder.RecordAsync(Store.Lidl, [reading with { Barcode = gtin }], clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
            found++;
        }

        log.LogInformation("Lidl catalog sweep: {Found} barcodes read from {Opened} product pages", found, wanted.Count);
    }

    private async Task RecordRunAsync(DateTimeOffset started, PullOutcome outcome, int seen, string message, CancellationToken ct)
    {
        db.PricePullRuns.Add(new PricePullRun
        {
            Id = Guid.NewGuid(),
            Store = Store.Lidl,
            StartedAt = started,
            FinishedAt = clock.GetUtcNow(),
            Outcome = outcome,
            Read = seen,
            Message = PriceRecorder.Truncate(message, 500),
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// The nightly Lidl catalog job. Separate from <see cref="PricePullJob"/> because that reads the shelf's listings and this the
/// shop's range; a sweep that fails leaves the price pull alone.
/// </summary>
public sealed class LidlCatalogJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LidlCatalogJob> log)
{
    public const string Id = "lidl-catalog-sweep";

    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LidlCatalogSweep>().RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The Lidl catalog sweep failed");
            await RecordFailureAsync(ex.Message, ct);
        }
    }

    private async Task RecordFailureAsync(string message, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            db.PricePullRuns.Add(new PricePullRun
            {
                Id = Guid.NewGuid(),
                Store = Store.Lidl,
                StartedAt = now,
                FinishedAt = now,
                Outcome = PullOutcome.Failed,
                Message = PriceRecorder.Truncate($"Unexpected error in the catalog sweep: {message}", 500),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not record the failed Lidl catalog sweep");
        }
    }

    /// <summary>Half an hour after the price pull, so the two never share the shop's politeness gap.</summary>
    public static void Schedule(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<LidlCatalogJob>(
            Id,
            job => job.RunAsync(CancellationToken.None),
            "30 3 * * *",
            new RecurringJobOptions { TimeZone = Dublin });
}
