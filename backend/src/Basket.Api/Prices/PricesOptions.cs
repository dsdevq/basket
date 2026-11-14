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

    /// <summary>The sitemap index of lidl.ie (robots.txt names it); the sweep reads the page sitemap it lists to find the food category pages.</summary>
    public string LidlSitemapIndex { get; set; } = "https://www.lidl.ie/static/sitemap.xml";

    /// <summary>A sitemap category page is walked when its hyphenated name holds one of these words.</summary>
    public string[] LidlFoodWords { get; set; } =
    [
        "food", "drink", "drinks", "fruit", "vegetables", "meat", "poultry", "fish", "seafood", "dairy", "cheese", "eggs", "bakery",
        "bread", "chilled", "frozen", "snacks", "sweets", "chocolate", "biscuits", "cereal", "pasta", "rice", "sauces", "tea",
        "coffee", "juice", "water", "wine", "beer", "cider", "spirits", "grocery", "pantry", "deli",
    ];

    /// <summary>Product pages one night may open to read a barcode. About 700 food products take a few nights, then only new products.</summary>
    public int LidlBarcodePagesPerNight { get; set; } = 200;
}
