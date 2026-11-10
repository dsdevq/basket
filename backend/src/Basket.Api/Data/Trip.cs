namespace Basket.Api.Data;

/// <summary>
/// A shopping trip that was closed with "Done shopping": the day it was filed and the items that were ticked.
/// Nothing else is kept (not the shops, not the prices), so the history is exactly what the list showed.
/// </summary>
public sealed class Trip
{
    public Guid Id { get; set; }

    public DateTimeOffset ClosedAt { get; set; }
}

/// <summary>One ticked line of a closed trip, copied off the list when the trip was closed.</summary>
public sealed class TripItem
{
    public Guid Id { get; set; }

    public Guid TripId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>Who ticked it; null once that person's account is gone.</summary>
    public Guid? CheckedByUserId { get; set; }
}
