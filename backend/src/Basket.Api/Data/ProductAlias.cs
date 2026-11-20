namespace Basket.Api.Data;

/// <summary>
/// Another name for a product, in the household's own words ("Яйця" for "Free Range Eggs"). One alias belongs to one product: the
/// same words never point at two. A typed list line whose text is an alias links to the product, the Shelf picker finds the
/// product by it, and (when it is in English) the matcher reads it as one more name of the product. There is one household per
/// database, so the "household" of an alias is the database itself.
/// </summary>
public sealed class ProductAlias
{
    public const int MaxLength = 80;

    /// <summary>A guard against a runaway client; a product rarely has more than a handful of other names.</summary>
    public const int MaxPerProduct = 20;

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    /// <summary>The words as the person typed them.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The words folded for comparison (<see cref="Basket.Api.Prices.ProductAliases.Normalize"/>); unique across aliases.</summary>
    public string NormalizedText { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }

    public Guid AddedByUserId { get; set; }
}
