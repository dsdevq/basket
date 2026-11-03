using Basket.Api.Auth;
using Hangfire;

namespace Basket.Api.Infrastructure;

/// <summary>Background work. M1 has one job; the price fetchers of M2 and the receipt pull of M4 are added here.</summary>
public sealed class InviteCleanupJob(InviteService invites, ILogger<InviteCleanupJob> log)
{
    public const string Id = "invite-cleanup";

    public async Task RunAsync(CancellationToken ct)
    {
        var removed = await invites.PurgeAsync(ct);
        log.LogInformation("Invite cleanup removed {Count} stale invites", removed);
    }

    public static void Schedule(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<InviteCleanupJob>(
            Id,
            job => job.RunAsync(CancellationToken.None),
            "30 4 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin") });
}
