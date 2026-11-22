namespace Basket.Api.Data;

/// <summary>
/// One Lidl Plus coupon as Lidl last listed it for the household's account. The table is a snapshot: each read of Lidl's list
/// replaces it, so a coupon Lidl no longer lists is gone, and <see cref="Activated"/> is what Lidl said (or what basket activated
/// since). "Pick one product" coupons are never stored: basket does not know which product the owner would pick.
/// </summary>
public sealed class LidlCoupon
{
    public const int MaxIdLength = 64;
    public const int MaxTitleLength = 200;
    public const int MaxTextLength = 500;

    /// <summary>Lidl's own id for the coupon.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>What the coupon gives, as Lidl words it ("20% off", "€1 off"). Null when Lidl gives no wording.</summary>
    public string? Discount { get; set; }

    public string? Description { get; set; }

    /// <summary>The first Dublin day the coupon works; null when Lidl states none.</summary>
    public DateOnly? ValidFrom { get; set; }

    /// <summary>The last Dublin day the coupon works; null when Lidl states none.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>In Ireland a coupon has to be activated before the till; only an activated one counts in a price.</summary>
    public bool Activated { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}
