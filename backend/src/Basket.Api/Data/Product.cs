using Basket.Api.Prices;

namespace Basket.Api.Data;

/// <summary>One thing the household buys, named the way they name it. Shops' own pages hang off it as listings.</summary>
public sealed class Product
{
    public const int MaxNameLength = 120;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }

    public Guid AddedByUserId { get; set; }
}

/// <summary>The page of one shop that sells a product: where the nightly job reads its price.</summary>
public sealed class StoreListing
{
    public const int MaxCategoryLength = 200;

    public const string CategorySeparator = " > ";

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Store Store { get; set; }

    public string Url { get; set; } = string.Empty;

    /// <summary>The shop's own name for it, learned on the first successful read.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Where the shop shows this product's photo (an https address, never the bytes), kept from the last read that
    /// carried one. A read without a photo leaves it as it was.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Where the shop files it, from the breadcrumb of its page, outermost first and joined with <see cref="CategorySeparator"/>
    /// ("Chilled Food &gt; Milk"). Null until a reader has sent one; a later reading without one keeps it.
    /// </summary>
    public string? Category { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Why the last read did not produce a price; null when it did.</summary>
    public string? LastError { get; set; }
}

/// <summary>One dated price reading. One row per listing per Dublin day; a second read the same day replaces it.</summary>
public sealed class PriceObservation
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public DateOnly ObservedOn { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public decimal? Price { get; set; }

    public decimal? WasPrice { get; set; }

    public decimal? LoyaltyPrice { get; set; }

    public string? LoyaltyScheme { get; set; }

    public string? PromoText { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidUntil { get; set; }

    public bool InStock { get; set; }
}

public enum PullOutcome
{
    /// <summary>Every linked product was read.</summary>
    Ok,

    /// <summary>Some were read, some were not.</summary>
    Partial,

    /// <summary>The shop's site refused automated access; basket does not try to get around it.</summary>
    Blocked,

    /// <summary>Nothing was read (site down, pages changed, unexpected error).</summary>
    Failed,
}

/// <summary>How one shop's part of one nightly run went. The page shows the latest one per shop.</summary>
public sealed class PricePullRun
{
    public Guid Id { get; set; }

    public Store Store { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public PullOutcome Outcome { get; set; }

    public int Listings { get; set; }

    public int Read { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// A price the household really paid, with its date. Filled from Lidl Plus receipts once the owner has connected Lidl Plus; one row
/// per product, shop and day.
/// </summary>
public sealed class PaidPrice
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Store Store { get; set; }

    public DateOnly PaidOn { get; set; }

    /// <summary>When the receipt behind this price was printed; of the receipts of one day, the latest keeps its price.</summary>
    public DateTimeOffset? PaidAt { get; set; }

    public decimal Price { get; set; }
}
