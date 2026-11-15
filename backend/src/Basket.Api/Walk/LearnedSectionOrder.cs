using Basket.Api.Prices;

namespace Basket.Api.Walk;

/// <summary>One line ticked in a closed trip: the shop it was bought at, the section its category there falls in, and when it was ticked.</summary>
public sealed record WalkTick(Guid TripId, Store Store, string Section, DateTimeOffset At);

/// <summary>A store's learned walk: the sections in the order they come, and how many trips it was learned from.</summary>
public sealed record LearnedOrder(IReadOnlyList<string> Sections, int Trips);

/// <summary>
/// Learns the order a shop's sections are walked from the order lines were ticked across closed trips, the section being the unit.
/// Pure: <see cref="WalkOrderLoader"/> reads the ticks, this decides. Derived on every read from the trips already kept, so nothing
/// is stored and a trip closed today counts at once.
///
/// A trip counts for a shop (<see cref="Qualifies"/>) only if it shows a walk: <see cref="MinItems"/> lines bought there, in
/// <see cref="MinSections"/> named sections, ticked over at least <see cref="MinSpan"/>. A list ticked off in one go on the sofa has
/// no order in it. A shop's order is learned once <see cref="MinTrips"/> such trips exist, from the latest <see cref="MaxTrips"/>
/// (shops move their shelves). Within a trip a section is where its middle tick falls, so one stray tick does not move it. Across
/// trips each pair of sections is a vote (this one came before that one), and a section ranks by the pairs it wins minus the ones it
/// loses (Copeland's rule), because a trip rarely visits every section. A section seen in fewer than <see cref="MinSeen"/> trips is
/// not placed by the learning and keeps its place in the seeded order after the learned ones. "Other" never learns: it stays last.
/// </summary>
public static class LearnedSectionOrder
{
    public const int MinTrips = 3;
    public const int MaxTrips = 10;
    public const int MinItems = 4;
    public const int MinSections = 3;
    public const int MinSeen = 2;
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(3);

    /// <summary>The order learned for <paramref name="store"/> from <paramref name="ticks"/>, or null while it has fewer than <see cref="MinTrips"/> trips that show a walk.</summary>
    public static LearnedOrder? Learn(Store store, IEnumerable<WalkTick> ticks)
    {
        var walks = ticks
            .Where(t => t.Store == store && t.Section != SeededSectionOrder.Other)
            .GroupBy(t => t.TripId)
            .Select(g => g.ToList())
            .Where(Qualifies)
            .OrderByDescending(g => g.Max(t => t.At))
            .Take(MaxTrips)
            .Select(Walk)
            .ToList();
        if (walks.Count < MinTrips)
        {
            return null;
        }

        var seeded = SeededSectionOrder.For(store);
        var seen = walks.SelectMany(w => w.Select(s => s.Section)).GroupBy(s => s).Where(g => g.Count() >= MinSeen).Select(g => g.Key).ToList();
        var wins = new Dictionary<(string, string), int>();
        foreach (var walk in walks)
        {
            for (var i = 0; i < walk.Count; i++)
            {
                for (var j = 0; j < walk.Count; j++)
                {
                    if (walk[i].At < walk[j].At)
                    {
                        wins[(walk[i].Section, walk[j].Section)] = wins.GetValueOrDefault((walk[i].Section, walk[j].Section)) + 1;
                    }
                }
            }
        }

        int Score(string a) => seen.Where(b => b != a).Sum(b => Math.Sign(wins.GetValueOrDefault((a, b)) - wins.GetValueOrDefault((b, a))));
        var learned = seen
            .OrderByDescending(Score)
            .ThenBy(s => SeededSectionOrder.RankOfSection(store, s))
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();
        var order = learned.Concat(seeded.Where(s => s != SeededSectionOrder.Other && !learned.Contains(s))).Append(SeededSectionOrder.Other).ToList();
        return new LearnedOrder(order, walks.Count);
    }

    /// <summary>A trip shows a walk at a shop: enough lines, across enough sections, ticked over long enough to have an order.</summary>
    private static bool Qualifies(List<WalkTick> trip) =>
        trip.Count >= MinItems
        && trip.Select(t => t.Section).Distinct().Count() >= MinSections
        && trip.Max(t => t.At) - trip.Min(t => t.At) >= MinSpan;

    /// <summary>The sections of a trip with the time of their middle tick (the earlier of the two middles when even).</summary>
    private static List<(string Section, DateTimeOffset At)> Walk(List<WalkTick> trip) =>
        trip.GroupBy(t => t.Section)
            .Select(g => (Section: g.Key, At: g.Select(t => t.At).Order().ElementAt((g.Count() - 1) / 2)))
            .ToList();
}
