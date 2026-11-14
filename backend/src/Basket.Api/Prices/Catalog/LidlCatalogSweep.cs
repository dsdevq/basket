using Basket.Api.Data;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Parsing;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Basket.Api.Prices.Catalog;

/// <summary>A shop whose whole catalog the server reads itself, once a night, into the combined catalog.</summary>
public interface ICatalogSweep
{
    Store Store { get; }

    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// The nightly Lidl catalog sweep. lidl.ie lists its whole range but shows an everyday price on only part of it, so this is a
/// range reader: the food listing pages (found in the page sitemap, plus the configured food root) are walked with
/// <c>?offset=</c> in steps of 12, each tile is stored as a catalog product through <see cref="CatalogRecorder"/> (the path the
/// PC reader's endpoint also ends in), and the sweep is closed through <see cref="CatalogIngest.CompleteAsync"/> so the
/// discontinued guard and the run row are the ones every shop has. Then the product pages of at most
/// <see cref="PricesOptions.LidlBarcodePagesPerNight"/> products with no stored barcode are opened to read the GTIN. It never
/// calls Lidl's search API, and a refusal ends the sweep (nothing is tried again or worked around).
/// </summary>
public sealed class LidlCatalogSweep(
    IPageFetcher fetcher,
    AppDbContext db,
    CatalogRecorder recorder,
    CatalogIngest ingest,
    IOptions<PricesOptions> options,
    TimeProvider clock,
    ILogger<LidlCatalogSweep> log) : ICatalogSweep
{
    /// <summary>Tiles per listing page; Lidl's server decides it, the sweep only steps by it.</summary>
    public const int PageSize = 12;

    /// <summary>A category is never walked past this many pages (the food range is about 60), so a page that repeats cannot loop.</summary>
    private const int MaxPagesPerCategory = 120;

    public Store Store => Store.Lidl;

    public async Task RunAsync(CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var settings = options.Value;
        var readings = new Dictionary<string, CatalogReading>(StringComparer.Ordinal);
        var refused = false;
        var pagesLeft = Math.Max(settings.LidlCatalogMaxPages, 0);
        string? note = null;

        var categories = new List<Uri>();
        var sitemap = await DiscoverCategoriesAsync(settings, ct);
        refused = sitemap.Refused;
        if (!refused)
        {
            categories.AddRange(settings.LidlCatalogRootPages.Select(p => new Uri(p)));
            categories.AddRange(sitemap.Pages.Where(p => !categories.Contains(p)));
            pagesLeft -= sitemap.Requests;
        }

        var rejected = 0;
        foreach (var category in categories)
        {
            for (var page = 0; page < MaxPagesPerCategory && !refused; page++)
            {
                if (pagesLeft-- <= 0)
                {
                    note ??= "The page budget ran out before every category was walked.";
                    break;
                }

                var result = await fetcher.GetAsync(page == 0 ? category : new Uri($"{category}?offset={page * PageSize}"), ct);
                if (result.Status == FetchStatus.Blocked)
                {
                    refused = true;
                    note = $"{result.Detail}. basket does not try to get around a block.";
                    break;
                }

                if (result.Status != FetchStatus.Ok)
                {
                    // A page that is down costs its own products only; the guard on closing keeps a thin sweep from delisting anything.
                    note ??= $"A Lidl listing page was not read ({result.Detail ?? "no detail"}).";
                    break;
                }

                var fresh = new List<CatalogReading>();
                foreach (var item in LidlParser.ParseCatalogTiles(result.Body!))
                {
                    if (item.ExternalId is { } id && readings.ContainsKey(id))
                    {
                        continue;
                    }

                    if (CatalogReading.TryCreate(Store.Lidl, item, out var reading, out _))
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
                    break; // past the last page: nothing new, so the next offset would only repeat or be empty
                }

                await recorder.RecordAsync(Store.Lidl, fresh, clock.GetUtcNow(), ct);
                await db.SaveChangesAsync(ct);
            }

            if (refused || pagesLeft < 0)
            {
                break;
            }
        }

        if (rejected > 0)
        {
            log.LogWarning("Lidl catalog sweep: {Rejected} tiles were unusable", rejected);
        }

        if (refused)
        {
            await RecordRefusedAsync(started, readings.Count, note ?? "Lidl refused automated access.", ct);
            return;
        }

        var closed = await ingest.CompleteAsync(Store.Lidl, readings.Count, started, ct);
        log.LogInformation("Lidl catalog sweep: {Seen} products, {Outcome}", closed.Seen, closed.Outcome);
        if (note is not null)
        {
            await AppendNoteAsync(started, note, ct);
        }

        await ReadBarcodesAsync(readings, ct);
    }

    private sealed record Discovery(IReadOnlyList<Uri> Pages, int Requests, bool Refused);

    /// <summary>
    /// The food category pages of the page sitemap: the index names the sitemaps, the one named "pages" lists the categories.
    /// A sitemap that cannot be read costs only the extra categories (the configured food root is still walked); a refusal ends the sweep.
    /// </summary>
    private async Task<Discovery> DiscoverCategoriesAsync(PricesOptions settings, CancellationToken ct)
    {
        var requests = 0;
        var index = await fetcher.GetAsync(new Uri(settings.LidlSitemapIndex), ct);
        requests++;
        if (index.Status == FetchStatus.Blocked)
        {
            return new Discovery([], requests, true);
        }

        if (index.Status != FetchStatus.Ok)
        {
            log.LogWarning("The Lidl sitemap index was not read: {Detail}", index.Detail);
            return new Discovery([], requests, false);
        }

        var pagesSitemap = LidlParser.SitemapLocations(index.Body!)
            .FirstOrDefault(u => Path.GetFileName(u.AbsolutePath).StartsWith("pages", StringComparison.OrdinalIgnoreCase));
        if (pagesSitemap is null)
        {
            log.LogWarning("The Lidl sitemap index lists no page sitemap");
            return new Discovery([], requests, false);
        }

        var sitemap = await fetcher.GetAsync(pagesSitemap, ct);
        requests++;
        if (sitemap.Status == FetchStatus.Blocked)
        {
            return new Discovery([], requests, true);
        }

        if (sitemap.Status != FetchStatus.Ok)
        {
            log.LogWarning("The Lidl page sitemap was not read: {Detail}", sitemap.Detail);
            return new Discovery([], requests, false);
        }

        return new Discovery(LidlParser.FoodCategoryPages(sitemap.Body!, settings.LidlFoodWords), requests, false);
    }

    /// <summary>
    /// Opens the product page of products with no stored barcode (at most the nightly cap, in product-number order) and stores the
    /// GTIN of the JSON-LD. A page that is gone, down or without a valid GTIN is skipped and tried again another night; a refusal ends it.
    /// </summary>
    private async Task ReadBarcodesAsync(Dictionary<string, CatalogReading> readings, CancellationToken ct)
    {
        var cap = Math.Max(options.Value.LidlBarcodePagesPerNight, 0);
        if (cap == 0 || readings.Count == 0)
        {
            return;
        }

        var ids = readings.Keys.ToList();
        var wanted = await db.StoreProducts
            .Where(p => p.Store == Store.Lidl && p.Barcode == null && ids.Contains(p.ExternalId))
            .OrderBy(p => p.ExternalId)
            .Select(p => p.ExternalId)
            .Take(cap)
            .ToListAsync(ct);

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

    private async Task RecordRefusedAsync(DateTimeOffset started, int seen, string message, CancellationToken ct)
    {
        db.PricePullRuns.Add(new PricePullRun
        {
            Id = Guid.NewGuid(),
            Store = Store.Lidl,
            StartedAt = started,
            FinishedAt = clock.GetUtcNow(),
            Outcome = PullOutcome.Blocked,
            Read = seen,
            Message = PriceRecorder.Truncate(message, 500),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds what the sweep could not do to the message of the run row it just closed.</summary>
    private async Task AppendNoteAsync(DateTimeOffset started, string note, CancellationToken ct)
    {
        var run = await db.PricePullRuns
            .Where(r => r.Store == Store.Lidl && r.StartedAt == started)
            .OrderByDescending(r => r.FinishedAt)
            .FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return;
        }

        run.Message = PriceRecorder.Truncate(run.Message is null ? note : $"{run.Message} {note}", 500);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// The nightly catalog job: runs each shop's <see cref="ICatalogSweep"/> on its own, so one failing leaves the others and the
/// price pull alone. Separate from <see cref="PricePullJob"/> because that reads the shelf's listings and this the shop's range.
/// </summary>
public sealed class CatalogSweepJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CatalogSweepJob> log)
{
    public const string Id = "catalog-sweep";

    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync(CancellationToken ct)
    {
        List<Store> stores;
        await using (var probe = scopes.CreateAsyncScope())
        {
            stores = probe.ServiceProvider.GetServices<ICatalogSweep>().Select(s => s.Store).ToList();
        }

        foreach (var store in stores)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetServices<ICatalogSweep>().First(s => s.Store == store).RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "The {Store} catalog sweep failed", store);
                await RecordFailureAsync(store, ex.Message, ct);
            }
        }
    }

    private async Task RecordFailureAsync(Store store, string message, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            db.PricePullRuns.Add(new PricePullRun
            {
                Id = Guid.NewGuid(),
                Store = store,
                StartedAt = now,
                FinishedAt = now,
                Outcome = PullOutcome.Failed,
                Message = PriceRecorder.Truncate($"Unexpected error in the catalog sweep: {message}", 500),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not record the failed {Store} catalog sweep", store);
        }
    }

    /// <summary>Half an hour after the price pull, so the two never share the shop's politeness gap.</summary>
    public static void Schedule(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<CatalogSweepJob>(
            Id,
            job => job.RunAsync(CancellationToken.None),
            "30 3 * * *",
            new RecurringJobOptions { TimeZone = Dublin });
}
