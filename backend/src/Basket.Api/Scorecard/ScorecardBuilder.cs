using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Promotions;

namespace Basket.Api.Scorecard;

/// <summary>How many wins one shop has among the compared lines, and its share of them (0-100, rounded).</summary>
public sealed record ScorecardStore(string Store, string StoreName, int Wins, int Share);

/// <summary>One month of closed trips: the Shelf lines bought, the ones that could be compared, and what the winners saved over the dearest shop.</summary>
public sealed record ScorecardMonth(string Month, int Trips, int Compared, decimal Saved);

/// <summary>
/// The shops scored on what the household really bought. <see cref="Compared"/> counts the Shelf lines of closed trips that two or
/// more shops priced like for like on the day of the trip; <see cref="NotCompared"/> counts the rest (typed lines, a line with fewer
/// than two comparable prices), which add nothing, never a guess. <see cref="Saved"/> is what buying each compared line at its
/// winner cost less than at the dearest shop. <see cref="Stores"/> is every shop in fixed order, most wins is <see cref="TopStore"/>
/// (null with no wins). <see cref="Trend"/> is one row per month of the window, oldest first.
/// </summary>
public sealed record ScorecardResponse(
    DateOnly AsOf,
    int StaleAfterDays,
    int Months,
    int Trips,
    int Compared,
    int NotCompared,
    decimal Saved,
    string? TopStore,
    IReadOnlyList<ScorecardStore> Stores,
    IReadOnlyList<ScorecardMonth> Trend);

/// <summary>One closed trip with the Shelf products it bought; the part of a trip the scorecard needs.</summary>
public sealed record ScorecardTrip(DateOnly Day, int Lines, IReadOnlyList<Guid> ProductIds);

/// <summary>
/// Scores the shops from closed trips. Pure: each bought product is compared across its shops with the promotions rule on the
/// readings known that day, so a price is the same number as on the Plan and By shop screens. A line is won by the shop with the
/// lowest unit price among the fresh, in-stock, comparable offers (equal prices go to the first shop in the fixed order, as the
/// plan does); it saves the winner's pack the difference to the dearest comparable shop's unit price.
/// </summary>
public static class ScorecardBuilder
{
    /// <summary>The trend and the totals cover this many months, the current one included.</summary>
    public const int Months = 6;

    public static ScorecardResponse Build(
        IReadOnlyList<ScorecardTrip> trips,
        IReadOnlyDictionary<Guid, Product> products,
        ILookup<Guid, StoreListing> listings,
        ILookup<Guid, PriceObservation> readings,
        DateOnly today)
    {
        var wins = Stores.All.ToDictionary(s => s.Slug(), _ => 0);
        var months = Window(today).ToDictionary(m => m, _ => new Tally());
        var compared = 0;
        var notCompared = 0;
        var saved = 0m;
        var tripCount = 0;

        foreach (var trip in trips)
        {
            var key = MonthKey(trip.Day);
            if (!months.TryGetValue(key, out var month))
            {
                continue;
            }

            tripCount++;
            month.Trips++;
            // A line with no Shelf product (typed, or its product deleted) was never priced, so it is not compared.
            var unproduct = trip.Lines - trip.ProductIds.Count;
            notCompared += unproduct;
            foreach (var productId in trip.ProductIds)
            {
                if (!products.TryGetValue(productId, out var product) || Compare(product, listings, readings, trip.Day) is not { } result)
                {
                    notCompared++;
                    continue;
                }

                wins[result.Winner]++;
                compared++;
                saved += result.Saved;
                month.Compared++;
                month.Saved += result.Saved;
            }
        }

        var stores = Stores.All
            .Select(s => new ScorecardStore(s.Slug(), s.DisplayName(), wins[s.Slug()], compared == 0 ? 0 : (int)Math.Round(100m * wins[s.Slug()] / compared)))
            .ToList();
        var top = stores.Where(s => s.Wins > 0).OrderByDescending(s => s.Wins).Select(s => s.Store).FirstOrDefault();
        var trend = months.Select(m => new ScorecardMonth(m.Key, m.Value.Trips, m.Value.Compared, m.Value.Saved)).ToList();
        return new ScorecardResponse(today, PromotionBuilder.StaleAfterDays, Months, tripCount, compared, notCompared, saved, top, stores, trend);
    }

    private sealed class Tally
    {
        public int Trips;
        public int Compared;
        public decimal Saved;
    }

    /// <summary>The winner's slug and what its pack saved against the dearest shop, or null when fewer than two shops were comparable that day.</summary>
    private static (string Winner, decimal Saved)? Compare(
        Product product,
        ILookup<Guid, StoreListing> listings,
        ILookup<Guid, PriceObservation> readings,
        DateOnly day)
    {
        var latest = new Dictionary<Guid, PriceObservation>();
        foreach (var listing in listings[product.Id])
        {
            // The reading known on the day of the trip: that day's, else the newest before it. How old it may be is the
            // promotions rule's stale limit, applied by Compare with the trip day as "today".
            var known = readings[listing.Id].Where(o => o.ObservedOn <= day).OrderByDescending(o => o.ObservedOn).FirstOrDefault();
            if (known is not null)
            {
                latest[listing.Id] = known;
            }
        }

        var ranked = PromotionBuilder.Compare(product, listings, latest, day).Offers.Where(o => o.Rank is not null).ToList();
        if (ranked.Count < 2)
        {
            return null;
        }

        var winner = ranked.OrderBy(o => o.Rank).ThenBy(o => StoreOrder(o.Store)).First();
        var dearest = ranked.Max(o => o.UnitPrice!.Value);
        var cheapest = winner.UnitPrice!.Value;
        var saving = cheapest > 0 ? winner.EachPrice * (dearest / cheapest - 1m) : 0m;
        return (winner.Store, Math.Round(saving, 2));
    }

    private static int StoreOrder(string slug) => Stores.All.ToList().FindIndex(s => s.Slug() == slug);

    public static string MonthKey(DateOnly day) => $"{day.Year:D4}-{day.Month:D2}";

    /// <summary>The window's month keys, oldest first, ending with the month of <paramref name="today"/>.</summary>
    public static IEnumerable<string> Window(DateOnly today) =>
        Enumerable.Range(0, Months).Select(i => MonthKey(today.AddMonths(i - (Months - 1))));

    /// <summary>The first day of the window.</summary>
    public static DateOnly WindowStart(DateOnly today)
    {
        var first = today.AddMonths(-(Months - 1));
        return new DateOnly(first.Year, first.Month, 1);
    }
}
