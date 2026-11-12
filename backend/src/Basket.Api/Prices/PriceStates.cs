using Basket.Api.Data;

namespace Basket.Api.Prices;

/// <summary>Whether a reading can be used today. The Prices page and the Promotions view both ask it the same way.</summary>
public static class PriceStates
{
    public const string Current = "current";
    public const string Upcoming = "upcoming";
    public const string Ended = "ended";
    public const string Unavailable = "unavailable";

    public static string Of(PriceObservation observation, DateOnly today) =>
        observation.ValidFrom is { } from && from > today ? Upcoming
        : observation.ValidUntil is { } until && until < today ? Ended
        : observation.Price is null && observation.LoyaltyPrice is null ? Unavailable
        : Current;
}
