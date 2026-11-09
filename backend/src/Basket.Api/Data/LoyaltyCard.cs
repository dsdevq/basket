using Basket.Api.Prices;

namespace Basket.Api.Data;

/// <summary>
/// A shop's loyalty card, kept once for the household. The number is personal data: it is stored only as
/// <see cref="NumberProtected"/> (ASP.NET Data Protection) and is never written to a log.
/// </summary>
public sealed class LoyaltyCard
{
    public const int MaxProtectedLength = 2048;

    public Guid Id { get; set; }

    /// <summary>One card per shop; the unique index on this column is what makes "added once" true.</summary>
    public Store Store { get; set; }

    public CardFormat Format { get; set; }

    /// <summary>The card number, encrypted. Plain text never reaches the database.</summary>
    public string NumberProtected { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid AddedByUserId { get; set; }
}

/// <summary>How the card is drawn as a code at the till.</summary>
public enum CardFormat
{
    Code128,
    Ean13,
    Qr,
}
