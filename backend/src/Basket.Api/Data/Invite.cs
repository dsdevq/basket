namespace Basket.Api.Data;

/// <summary>
/// A one-time link that lets one person register a passkey. Only the SHA-256 of the token is stored,
/// so a database read never yields a usable link.
/// </summary>
public sealed class Invite
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid Id { get; set; }

    /// <summary>Lower-case hex SHA-256 of the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The role the invitee receives: <see cref="Roles.Owner"/> only from the first-run bootstrap.</summary>
    public string Role { get; set; } = Roles.Member;

    /// <summary>What the owner calls the invitee, to tell pending links apart. Not the invitee's own name.</summary>
    public string? Label { get; set; }

    /// <summary>Null for the first-run bootstrap invite.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public Guid? ConsumedByUserId { get; set; }
}
