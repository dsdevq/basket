namespace Basket.Api.Prices;

/// <summary>
/// What a shop's page said about one product on the day it was read. <see cref="Price"/> is what anyone pays,
/// <see cref="LoyaltyPrice"/> what a card holder pays (when the shop shows it without a login). <see cref="ImageUrl"/>
/// is the product photo's address when the page showed one, already checked by <see cref="ImageUrls"/>. <see cref="Category"/>
/// is the page's breadcrumb as a path ("Chilled Food > Milk"), when the reader found one.
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
    bool InStock,
    string? ImageUrl = null,
    string? Category = null);
