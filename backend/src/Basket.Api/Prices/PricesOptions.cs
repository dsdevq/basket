namespace Basket.Api.Prices;

public sealed class PricesOptions
{
    public const string Section = "Prices";

    /// <summary>The gap between two requests to the same shop's site. The whole nightly pull is a few dozen requests.</summary>
    public double MinRequestIntervalSeconds { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Lidl's weekly-offer and food pages. Their product tiles carry the price, the Lidl Plus price and the dates.
    /// A product that is on none of them is read from its own page instead.
    /// </summary>
    public string[] LidlOfferPages { get; set; } =
    [
        "https://www.lidl.ie/c/store/lidl-plus-offers/a10073407",
        "https://www.lidl.ie/c/store/super-savers/a10028883",
        "https://www.lidl.ie/c/food-drink/s10068374",
    ];
}
