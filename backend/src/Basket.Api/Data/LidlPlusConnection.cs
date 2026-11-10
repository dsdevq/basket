namespace Basket.Api.Data;

/// <summary>
/// The household's Lidl Plus sign-in: the one refresh token the owner pasted, kept so basket can ask Lidl for an access
/// token later (receipts and the Lidl Plus offers read through it). There is at most one row, because there is one Lidl Plus
/// account. The token is a password: it is stored only as <see cref="RefreshTokenProtected"/> (ASP.NET Data Protection),
/// is never returned by any route and is never written to a log.
/// </summary>
public sealed class LidlPlusConnection
{
    /// <summary>The primary key every row would share, so a second row cannot exist.</summary>
    public static readonly Guid SingletonId = new("11d1f1a5-0000-4000-8000-00000000f1a5");

    public const int MaxProtectedLength = 4096;

    public Guid Id { get; set; } = SingletonId;

    /// <summary>The refresh token, encrypted. Lidl rotates it on use, so this is replaced each time Lidl hands out a new one.</summary>
    public string RefreshTokenProtected { get; set; } = string.Empty;

    /// <summary>When the owner pasted the token that started this connection.</summary>
    public DateTimeOffset ConnectedAt { get; set; }

    /// <summary>The last time Lidl accepted the token. Null never happens for a stored row; a connection is made by a token Lidl accepted.</summary>
    public DateTimeOffset VerifiedAt { get; set; }

    /// <summary>Set when Lidl refused the token (it expired, or was used elsewhere); the owner has to paste a new one. Null while it works.</summary>
    public string? Problem { get; set; }

    public const string TokenRejected = "token_rejected";
}
