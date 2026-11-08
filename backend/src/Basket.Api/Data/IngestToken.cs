namespace Basket.Api.Data;

/// <summary>
/// A credential for the PC-side price reader, issued by the owner. It has one scope: posting Tesco, Aldi and Dunnes price
/// readings to <c>/api/ingest/prices</c>. It reads nothing and opens no other endpoint. Only the SHA-256 of the token
/// is stored, so a database read never yields a usable token; revoking deletes the row.
/// </summary>
public sealed class IngestToken
{
    /// <summary>Lower-case hex SHA-256 of the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid Id { get; set; }

    /// <summary>What the owner calls the reader this token is for, to tell tokens apart.</summary>
    public string? Label { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
