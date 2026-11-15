using Basket.Api.Prices;

namespace Basket.Api.Data;

/// <summary>
/// A shopping trip. The one with no <see cref="ClosedAt"/> is the open trip: every tick on the list records the item, the shop it
/// was bought at and the time in it, and "Done shopping" closes it, so the history is exactly what was ticked. A trip can also come
/// from a Lidl Plus receipt (<see cref="Source"/>), filed closed on the day of the receipt with the lines the till printed.
/// </summary>
public sealed class Trip
{
    public Guid Id { get; set; }

    /// <summary>When the trip was closed (the day it is filed under); null while it is the open trip, which the history does not show.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Where the trip came from: null when it was closed on the list, <see cref="TripSources.LidlPlus"/> for a receipt.</summary>
    public string? Source { get; set; }

    /// <summary>The source's own id for the trip (Lidl's receipt id), so the same receipt is never filed twice. Null for a trip closed on the list.</summary>
    public string? ExternalId { get; set; }

    /// <summary>What the receipt came to, as printed. Null for a trip closed on the list, which keeps no prices.</summary>
    public decimal? Total { get; set; }
}

public static class TripSources
{
    public const string LidlPlus = "lidl_plus";
}

public static class TripQueries
{
    /// <summary>The trips in the history: closed ones. The open trip is the list's own record of what is ticked and is not history yet.</summary>
    public static IQueryable<Trip> Closed(this IQueryable<Trip> trips) => trips.Where(t => t.ClosedAt != null);
}

/// <summary>One ticked line of a trip: written when the item is ticked on the list, kept when the trip closes.</summary>
public sealed class TripItem
{
    public Guid Id { get; set; }

    public Guid TripId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>Who ticked it; null once that person's account is gone.</summary>
    public Guid? CheckedByUserId { get; set; }

    /// <summary>
    /// The product the list line was picked as, so reloading the trip puts a Shelf line back as that product. Null for a
    /// free-text line, for a trip closed before lines carried one, and once the product is deleted.
    /// </summary>
    public Guid? ProductId { get; set; }

    /// <summary>The shop the line was picked from (null when picked from "All"); only meaningful with <see cref="ProductId"/>.</summary>
    public Store? Store { get; set; }

    /// <summary>
    /// The shop the item was bought at: the one the line was picked from, else the one the plan suggested, until the person changes
    /// it. Null when neither named one, and for a trip from before ticking recorded it or one made from a receipt.
    /// </summary>
    public Store? BoughtStore { get; set; }

    /// <summary>
    /// The list line this record is for while the trip is open, so un-ticking or deleting the line takes it back out. Set null when
    /// the line leaves the list (which is what closing the trip does), so a closed trip's items point at nothing.
    /// </summary>
    public Guid? ListItemId { get; set; }
}
