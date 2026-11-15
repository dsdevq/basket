using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Lists;

/// <summary>
/// The open trip: the one trip with no close date, where every tick on the list is recorded until "Done shopping" closes it.
/// Everything that changes what is ticked (tick, un-tick, delete a line, close) takes <see cref="LockAsync"/> first, so the list's
/// ticked lines and the open trip's items move together and two phones cannot start two open trips.
/// </summary>
public static class OpenTrips
{
    /// <summary>Arbitrary and fixed: the key of the transaction-scoped lock that serialises changes to the ticked lines.</summary>
    private const long LockKey = 0x7269_7073;

    /// <summary>Waits for any other change to the ticked lines to finish; released when the caller's transaction ends.</summary>
    public static async Task LockAsync(AppDbContext db, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);

    /// <summary>The open trip, started now when there is none. Call inside a transaction that holds <see cref="LockAsync"/>.</summary>
    public static async Task<Trip> GetOrStartAsync(AppDbContext db, CancellationToken ct)
    {
        var open = await db.Trips.SingleOrDefaultAsync(t => t.ClosedAt == null, ct);
        if (open is not null)
        {
            return open;
        }

        open = new Trip { Id = Guid.NewGuid() };
        db.Trips.Add(open);
        await db.SaveChangesAsync(ct);
        return open;
    }
}
