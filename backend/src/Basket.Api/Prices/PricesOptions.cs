namespace Basket.Api.Prices;

public sealed class PricesOptions
{
    public const string Section = "Prices";

    /// <summary>The gap between two requests to the same shop's site. The whole nightly pull is a few dozen requests.</summary>
    public double MinRequestIntervalSeconds { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Where Aldi's product API lives. asl.api.aldi.ie answers the server while www.aldi.ie does not (docs/decisions.md). The
    /// category tree is <c>{base}/product-category-tree</c>, the range <c>{base}/product-search</c>.
    /// </summary>
    public string AldiApiBase { get; set; } = "https://asl.api.aldi.ie/v3";

    /// <summary>
    /// The most requests one Aldi sweep may make, the category tree included. Aldi's terms were read as allowing a polite sweep;
    /// the owner accepted about 60 requests a day for a two-person household. A range larger than this many pages is cut short
    /// (and the 90 percent guard then keeps the unread rest from being delisted).
    /// </summary>
    public int AldiMaxRequests { get; set; } = 60;

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
