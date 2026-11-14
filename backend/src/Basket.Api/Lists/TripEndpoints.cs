using System.Security.Claims;
using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Lists;

/// <summary><see cref="ProductId"/> is the Shelf product the line was picked as; null for a typed (free-text) line, which a closed trip offers to promote to the Shelf.</summary>
public sealed record TripItemResponse(Guid Id, string Text, DateTimeOffset? CheckedAt, Person? CheckedBy, Guid? ProductId);

/// <summary><see cref="Source"/> is null for a trip closed on the list and <c>lidl_plus</c> for one made from a receipt, which also has its <see cref="Total"/>.</summary>
public sealed record TripResponse(Guid Id, DateTimeOffset ClosedAt, string? Source, decimal? Total, IReadOnlyList<TripItemResponse> Items);

/// <summary>Done shopping and the history of closed trips. A trip is a copy of the ticked items, filed on the day it was closed.</summary>
public static class TripEndpoints
{
    /// <summary>Trips per page of the history; older ones load with <c>before</c>.</summary>
    public const int PageSize = 20;

    public static void MapTripEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/list/close", CloseTrip);
        var trips = api.MapGroup("/trips");
        trips.MapGet("/", GetTrips);
        trips.MapPost("/{id:guid}/reload", Reload);
    }

    private static async Task<List<TripResponse>> Load(AppDbContext db, IReadOnlyList<Trip> trips, CancellationToken ct)
    {
        var tripIds = trips.Select(t => t.Id).ToList();
        var rows = await (
            from i in db.TripItems.AsNoTracking().Where(i => tripIds.Contains(i.TripId))
            join c in db.Users on i.CheckedByUserId equals c.Id into checkers
            from c in checkers.DefaultIfEmpty()
            select new
            {
                i.TripId,
                Item = new TripItemResponse(i.Id, i.Text, i.CheckedAt, c == null ? null : new Person(c.Id, c.DisplayName), i.ProductId),
            }).ToListAsync(ct);
        return trips.Select(trip => new TripResponse(
            trip.Id,
            trip.ClosedAt!.Value,
            trip.Source,
            trip.Total,
            rows.Where(r => r.TripId == trip.Id).Select(r => r.Item)
                .OrderBy(i => i.CheckedAt).ThenBy(i => i.Text, StringComparer.Ordinal).ToList())).ToList();
    }

    /// <summary>
    /// Closes the open trip: the items ticked since the last one are in it already (each tick recorded one), so closing dates it,
    /// takes the ticked items off the list and starts nothing new; the next tick opens the next trip. What is not ticked stays for
    /// the next shop (AnyList and OurGroceries do the same). One transaction: the trip is closed exactly when the items left the list.
    /// </summary>
    private static async Task<IResult> CloseTrip(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OpenTrips.LockAsync(db, ct);

        var ticked = await db.ListItems.Where(i => i.IsChecked).OrderBy(i => i.CheckedAt).ToListAsync(ct);
        if (ticked.Count == 0)
        {
            return Results.Json(new { error = "nothing_ticked", message = "Tick what you bought first." }, statusCode: 409);
        }

        // The open trip is the record of the ticked lines. Make it say exactly that: a record whose line is gone is dropped, and
        // a ticked line with none (ticked before trips were recorded) is added, bought at the shop it was picked from.
        var trip = await OpenTrips.GetOrStartAsync(db, ct);
        var recorded = await db.TripItems.Where(i => i.TripId == trip.Id).ToListAsync(ct);
        var tickedIds = ticked.Select(i => i.Id).ToHashSet();
        db.TripItems.RemoveRange(recorded.Where(r => r.ListItemId is not { } line || !tickedIds.Contains(line)));
        var known = recorded.Select(r => r.ListItemId).OfType<Guid>().ToHashSet();
        db.TripItems.AddRange(ticked.Where(i => !known.Contains(i.Id)).Select(i => new TripItem
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            Text = i.Text,
            CheckedAt = i.CheckedAt,
            CheckedByUserId = i.CheckedByUserId,
            ProductId = i.ProductId,
            // A shop only means something with its product (the add endpoint refuses one without).
            Store = i.ProductId is null ? null : i.Store,
            BoughtStore = i.ProductId is null ? null : i.Store,
            ListItemId = i.Id,
        }));
        trip.ClosedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // Someone un-ticking an item behind the lock (nothing does today) makes the delete touch fewer rows than were filed.
        // Then nothing is closed: the trip rolls back and the caller re-reads the list.
        var removed = await db.ListItems.Where(i => tickedIds.Contains(i.Id) && i.IsChecked).ExecuteDeleteAsync(ct);
        if (removed != tickedIds.Count)
        {
            await transaction.RollbackAsync(ct);
            return Results.Json(new { error = "list_changed", message = "The list just changed. Look again and retry." }, statusCode: 409);
        }

        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        var created = (await Load(db, [trip], ct)).Single();
        return Results.Created($"/api/trips/{trip.Id}", created);
    }

    /// <summary>Newest first, <see cref="PageSize"/> at a time. <c>before</c> is the <c>closedAt</c> of the last trip already shown.</summary>
    private static async Task<IResult> GetTrips(DateTimeOffset? before, AppDbContext db, CancellationToken ct)
    {
        var query = db.Trips.AsNoTracking().Closed();
        if (before is { } cursor)
        {
            query = query.Where(t => t.ClosedAt < cursor);
        }

        var page = await query.OrderByDescending(t => t.ClosedAt).ThenByDescending(t => t.Id).Take(PageSize + 1).ToListAsync(ct);
        var shown = page.Take(PageSize).ToList();
        return Results.Ok(new { trips = await Load(db, shown, ct), hasMore = page.Count > PageSize });
    }

    /// <summary>
    /// "Same as last time": puts the trip's items back on the list as to-buy lines added by the caller. A Shelf line comes
    /// back as its product and shop, so the Shelf tile sees it; a line from before trips kept that, or a free-text one,
    /// comes back as text. Nothing already waiting is added twice: a product line is the same as an open line for that
    /// product and shop, a text line as one with the same text (any letter case), so a second tap adds nothing.
    /// </summary>
    private static async Task<IResult> Reload(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var items = await db.TripItems.AsNoTracking().Where(i => i.TripId == id && db.Trips.Any(t => t.Id == id && t.ClosedAt != null))
            .OrderBy(i => i.CheckedAt).ThenBy(i => i.Text)
            .Select(i => new { i.Text, i.ProductId, i.Store })
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            return Results.NotFound();
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Two phones reloading at once can both see a line missing; the database's one-open-line-per-product index then
        // refuses the second insert, and the retry finds the first phone's lines waiting and adds what is left.
        for (var attempt = 1; ; attempt++)
        {
            var onList = await db.ListItems.AsNoTracking().Select(i => new { i.Text, i.IsChecked, i.ProductId, i.Store }).ToListAsync(ct);
            var waitingText = onList.Where(i => !i.IsChecked).Select(i => i.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var waitingProducts = onList.Where(i => !i.IsChecked && i.ProductId is not null)
                .Select(i => (i.ProductId, i.Store)).ToHashSet();

            var toAdd = new List<(string Text, Guid? ProductId, Store? Store)>();
            foreach (var item in items)
            {
                if (item.ProductId is { } productId)
                {
                    if (waitingProducts.Add((productId, item.Store)))
                    {
                        toAdd.Add((item.Text, productId, item.Store));
                        waitingText.Add(item.Text);
                    }
                }
                else if (waitingText.Add(item.Text))
                {
                    toAdd.Add((item.Text, null, null));
                }
            }

            if (onList.Count + toAdd.Count > ListEndpoints.MaxItems)
            {
                return Results.Json(new { error = "list_full", message = "The list is full. Delete some items first." }, statusCode: 409);
            }

            var now = clock.GetUtcNow();
            // A tick apart (a microsecond) so the list keeps the trip's order instead of an arbitrary one.
            db.ListItems.AddRange(toAdd.Select((line, index) => new ListItem
            {
                Id = Guid.NewGuid(),
                Text = line.Text,
                AddedAt = now.AddTicks(index * 10L),
                AddedByUserId = userId,
                ProductId = line.ProductId,
                Store = line.Store,
            }));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt == 1 && ListEndpoints.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                continue;
            }

            return Results.Ok(new { added = toAdd.Count, alreadyOnList = items.Count - toAdd.Count });
        }
    }
}
