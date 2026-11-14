namespace Basket.Api.Prices;

public sealed class PricesOptions
{
    public const string Section = "Prices";

    /// <summary>The gap between two requests to the same shop's site. The whole nightly pull is a few dozen requests.</summary>
    public double MinRequestIntervalSeconds { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Product pages one night may open to read a barcode. About 700 food products take a few nights, then only new products.</summary>
    public int LidlBarcodePagesPerNight { get; set; } = 200;
}
