using Basket.Api.Data;

namespace Basket.Api.Prices.Sources;

public sealed record ListingToCheck(Guid ListingId, Uri Url);

/// <summary>What reading one listing produced: a quote, or the reason there is none.</summary>
public sealed record ListingPull(PriceQuote? Quote, string? Error)
{
    public static ListingPull Priced(PriceQuote quote) => new(quote, null);

    public static ListingPull Failed(string error) => new(null, error);

    /// <summary>The source's pages do not list this one (another reader prices it): nothing is recorded for it, not even a failure.</summary>
    public static ListingPull Unlisted { get; } = new(null, null);

    public bool IsUnlisted => Quote is null && Error is null;
}

public sealed record StorePull(PullOutcome Outcome, string? Message, IReadOnlyDictionary<Guid, ListingPull> Listings);

/// <summary>Turns a shop's site into quotes for the listings basket holds. One per shop; a shop failing never affects another.</summary>
public interface IStorePriceSource
{
    Store Store { get; }

    /// <summary>The source reads the shop's whole catalog (not only linked pages), so the job runs it even with nothing linked yet.</summary>
    bool SweepsCatalog => false;

    Task<StorePull> PullAsync(IReadOnlyList<ListingToCheck> listings, CancellationToken ct);
}

internal static class StorePulls
{
    public static string BlockedMessage(string detail) => $"{detail}. basket does not try to get around a block.";

    /// <summary>The site refused us: every listing gets the same reason and the shop's run is "blocked".</summary>
    public static StorePull Blocked(IReadOnlyList<ListingToCheck> listings, string detail)
    {
        var message = BlockedMessage(detail);
        return new StorePull(PullOutcome.Blocked, message, listings.ToDictionary(l => l.ListingId, _ => ListingPull.Failed(message)));
    }
}
