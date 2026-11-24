namespace Basket.Api.Data;

/// <summary>
/// A proposed pair ("Яйця" is probably "Free Range Eggs") that a person turned down, so it is never proposed again. Keyed by the
/// folded words and the product, not by any trip, so more trips with the same words do not bring it back. Deleting the product
/// removes its rejections. There is one household per database, so there is no household column.
/// </summary>
public sealed class AliasRejection
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    /// <summary>The proposed words folded for comparison (<see cref="Basket.Api.Prices.ProductAliases.Normalize"/>).</summary>
    public string NormalizedText { get; set; } = string.Empty;

    public DateTimeOffset RejectedAt { get; set; }
}
