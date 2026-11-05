namespace Basket.Api.Prices;

/// <summary>
/// What a shop's page said about one product on the day it was read. <see cref="Price"/> is what anyone pays,
/// <see cref="LoyaltyPrice"/> what a card holder pays (when the shop shows it without a login).
/// </summary>
public sealed record PriceQuote(
    string? Title,
    decimal? Price,
    decimal? WasPrice,
    decimal? LoyaltyPrice,
    string? LoyaltyScheme,
    string? PromoText,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    bool InStock);
