using Basket.Api.Prices;

namespace Basket.Api.Walk;

/// <summary>
/// The order each shop is walked in, as the basket is sorted: the order learned from the shop's trips where there are enough
/// (<see cref="LearnedSectionOrder"/>), the seeded one (<see cref="SeededSectionOrder"/>) where there are not. A shop falls back on
/// its own, so one well-known shop does not wait for the others.
/// </summary>
public sealed class WalkOrders
{
    /// <summary>No trips known: every shop in its seeded order.</summary>
    public static WalkOrders Seeded { get; } = new(new Dictionary<Store, LearnedOrder>());

    private readonly IReadOnlyDictionary<Store, LearnedOrder> _learned;

    public WalkOrders(IReadOnlyDictionary<Store, LearnedOrder> learned) => _learned = learned;

    /// <summary>Learns each shop's order from <paramref name="ticks"/>; a shop with too few trips is left out and stays seeded.</summary>
    public static WalkOrders From(IReadOnlyCollection<WalkTick> ticks)
    {
        var learned = new Dictionary<Store, LearnedOrder>();
        foreach (var store in Stores.All)
        {
            if (LearnedSectionOrder.Learn(store, ticks) is { } order)
            {
                learned[store] = order;
            }
        }

        return new WalkOrders(learned);
    }

    public bool IsLearned(Store store) => _learned.ContainsKey(store);

    /// <summary>How many trips <paramref name="store"/>'s order was learned from; 0 while it is the seeded one.</summary>
    public int TripsOf(Store store) => _learned.TryGetValue(store, out var order) ? order.Trips : 0;

    /// <summary>The sections of <paramref name="store"/> in the order they come, "Other" last.</summary>
    public IReadOnlyList<string> For(Store store) => _learned.TryGetValue(store, out var order) ? order.Sections : SeededSectionOrder.For(store);

    /// <summary>The position of a section in <paramref name="store"/>'s walk; a name no order lists sorts last, with "Other".</summary>
    public int RankOfSection(Store store, string section)
    {
        var order = For(store);
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == section)
            {
                return i;
            }
        }

        return order.Count - 1;
    }
}
