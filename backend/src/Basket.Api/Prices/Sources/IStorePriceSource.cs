using Basket.Api.Data;

namespace Basket.Api.Prices.Sources;

/// <summary><paramref name="NeedsCategory"/>: no reading has stored a category for it yet, so a source that would not otherwise open its page may do so once.</summary>
public sealed record ListingToCheck(Guid ListingId, Uri Url, bool NeedsCategory = false);

/// <summary>What reading one listing produced: a quote, or the reason there is none.</summary>
public sealed record ListingPull(PriceQuote? Quote, string? Error)
{
    public static ListingPull Priced(PriceQuote quote) => new(quote, null);

    public static ListingPull Failed(string error) => new(null, error);
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
    /// <summary>The site refused us: every listing gets the same reason and the shop's run is "blocked".</summary>
    public static StorePull Blocked(IReadOnlyList<ListingToCheck> listings, string detail)
    {
        var message = $"{detail}. basket does not try to get around a block.";
        return new StorePull(PullOutcome.Blocked, message, listings.ToDictionary(l => l.ListingId, _ => ListingPull.Failed(message)));
    }

    public static StorePull Summarize(IReadOnlyList<ListingToCheck> listings, Dictionary<Guid, ListingPull> results, string? failureHint = null)
    {
        var read = results.Values.Count(r => r.Quote is not null);
        var outcome = read == listings.Count ? PullOutcome.Ok : read > 0 ? PullOutcome.Partial : PullOutcome.Failed;
        var message = outcome == PullOutcome.Ok
            ? null
            : failureHint ?? results.Values.Select(r => r.Error).FirstOrDefault(e => e is not null);
        return new StorePull(outcome, message, results);
    }
}
