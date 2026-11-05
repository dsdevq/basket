using Basket.Api.Data;
using Basket.Api.Prices.Sources;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices;

/// <summary>
/// The nightly price pull. Each shop is read and saved on its own, so a shop whose site is down, blocking or changed
/// leaves the others' prices in place. What the last reading of a shop was stays in <see cref="PricePullRun"/>.
/// </summary>
public sealed class PricePullJob(
    IServiceScopeFactory scopes,
    IEnumerable<IStorePriceSource> sources,
    TimeProvider clock,
    ILogger<PricePullJob> log)
{
    public const string Id = "price-pull";

    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var source in sources)
        {
            try
            {
                await PullStoreAsync(source, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "The {Store} price pull failed", source.Store);
                await RecordFailureAsync(source.Store, ex.Message, ct);
            }
        }
    }

    private async Task PullStoreAsync(IStorePriceSource source, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var started = clock.GetUtcNow();
        var listings = await db.StoreListings.Where(l => l.Store == source.Store).ToListAsync(ct);
        if (listings.Count == 0)
        {
            return;
        }

        var pull = await source.PullAsync(
            listings.Select(l => new ListingToCheck(l.Id, new Uri(l.Url))).ToList(),
            ct);

        var finished = clock.GetUtcNow();
        var recorder = scope.ServiceProvider.GetRequiredService<PriceRecorder>();
        var read = 0;
        foreach (var listing in listings)
        {
            if (!pull.Listings.TryGetValue(listing.Id, out var result))
            {
                PriceRecorder.RecordFailure(listing, pull.Message ?? "Not read.", finished);
            }
            else if (result.Quote is { } quote)
            {
                read++;
                await recorder.RecordAsync(listing, quote, finished, ct);
            }
            else
            {
                PriceRecorder.RecordFailure(listing, result.Error ?? "Not read.", finished);
            }
        }

        db.PricePullRuns.Add(new PricePullRun
        {
            Id = Guid.NewGuid(),
            Store = source.Store,
            StartedAt = started,
            FinishedAt = finished,
            Outcome = pull.Outcome,
            Listings = listings.Count,
            Read = read,
            Message = PriceRecorder.Truncate(pull.Message, 500),
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("{Store}: {Outcome}, {Read} of {Listings} read", source.Store, pull.Outcome, read, listings.Count);
    }

    /// <summary>The shop's part broke before it could say how it went (database error, unexpected exception).</summary>
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
                Message = PriceRecorder.Truncate($"Unexpected error: {message}", 500),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not record the failed {Store} price pull", store);
        }
    }

    public static void Schedule(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<PricePullJob>(
            Id,
            job => job.RunAsync(CancellationToken.None),
            "0 3 * * *",
            new RecurringJobOptions { TimeZone = Dublin });
}
