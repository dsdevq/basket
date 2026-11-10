using Basket.Api.Prices;

namespace Basket.Api.Data;

/// <summary>
/// A shopping trip that was closed with "Done shopping": the day it was filed and the items that were ticked, so the history
/// is exactly what the list showed. A trip can also come from a Lidl Plus receipt (<see cref="Source"/>), filed on the day of the
/// receipt with the lines the till printed.
/// </summary>
public sealed class Trip
{
    public Guid Id { get; set; }

    public DateTimeOffset ClosedAt { get; set; }

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

/// <summary>One ticked line of a closed trip, copied off the list when the trip was closed.</summary>
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
}
