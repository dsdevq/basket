using Basket.Api.Prices;

namespace Basket.Api.Data;

/// <summary>
/// One line of the shared shopping list. Free text, or a product picked from the Shelf: then <see cref="ProductId"/> links
/// it to the product and <see cref="Store"/> remembers the shop it was picked from (null when picked from "All").
/// </summary>
public sealed class ListItem
{
    public const int MaxTextLength = 200;

    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;

    public bool IsChecked { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public Guid AddedByUserId { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public Guid? CheckedByUserId { get; set; }

    /// <summary>The product this line was picked as; null for a free-text line. Set null if the product is deleted.</summary>
    public Guid? ProductId { get; set; }

    public Store? Store { get; set; }
}
