using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices;

/// <summary>
/// The one way a reading enters the history, whoever took it: the nightly Lidl job, or the write-only ingest
/// endpoint fed by the PC-side reader for Tesco and Aldi. Applies to the context; the caller saves.
/// </summary>
public sealed class PriceRecorder(AppDbContext db)
{
    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    /// <summary>The Dublin calendar day a reading belongs to (shops change prices at local midnight).</summary>
    public static DateOnly DublinDay(DateTimeOffset moment) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, Dublin).DateTime);

    /// <summary>Stores the reading as that day's one observation for the listing, replacing an earlier one of the same day.</summary>
    public async Task RecordAsync(StoreListing listing, PriceQuote quote, DateTimeOffset observedAt, CancellationToken ct)
    {
        var day = DublinDay(observedAt);
        var observation = await db.PriceObservations.SingleOrDefaultAsync(o => o.ListingId == listing.Id && o.ObservedOn == day, ct);
        if (observation is null)
        {
            observation = new PriceObservation { Id = Guid.NewGuid(), ListingId = listing.Id, ObservedOn = day };
            db.PriceObservations.Add(observation);
        }

        observation.ObservedAt = observedAt;
        observation.Price = quote.Price;
        observation.WasPrice = quote.WasPrice;
        observation.LoyaltyPrice = quote.LoyaltyPrice;
        observation.LoyaltyScheme = Truncate(quote.LoyaltyScheme, 32);
        observation.PromoText = Truncate(quote.PromoText, 200);
        observation.ValidFrom = quote.ValidFrom;
        observation.ValidUntil = quote.ValidUntil;
        observation.InStock = quote.InStock;

        listing.LastCheckedAt = observedAt;
        listing.LastError = null;
        listing.Title = Truncate(quote.Title, 300) ?? listing.Title;
    }

    /// <summary>A reading that did not work: the last known price stays, the listing says why it is stale.</summary>
    public static void RecordFailure(StoreListing listing, string error, DateTimeOffset at)
    {
        listing.LastCheckedAt = at;
        listing.LastError = Truncate(error, 500);
    }

    public static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
