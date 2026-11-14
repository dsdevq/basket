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
        Of(observation.Price, observation.LoyaltyPrice, observation.ValidFrom, observation.ValidUntil, today);

    /// <summary>The same rule for any dated reading (a catalog price row has the same fields).</summary>
    public static string Of(decimal? price, decimal? loyaltyPrice, DateOnly? validFrom, DateOnly? validUntil, DateOnly today) =>
        validFrom is { } from && from > today ? Upcoming
        : validUntil is { } until && until < today ? Ended
        : price is null && loyaltyPrice is null ? Unavailable
        : Current;
}
