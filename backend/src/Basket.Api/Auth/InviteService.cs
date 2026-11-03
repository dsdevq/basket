using System.Security.Cryptography;
using System.Text;
using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Auth;

public static class InviteTokens
{
    public const int MaxLength = 128;
    public const int MinBootstrapLength = 32;

    /// <summary>256 random bits, base64url: safe to put in a URL fragment.</summary>
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The invite lifecycle: mint, find while usable, consume exactly once, revoke, purge.</summary>
public sealed class InviteService(AppDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan PurgeAfter = TimeSpan.FromDays(7);

    public async Task<(Invite Invite, string Token)> CreateAsync(Guid createdBy, string? label, CancellationToken ct)
    {
        var token = InviteTokens.Generate();
        var now = clock.GetUtcNow();
        var invite = new Invite
        {
            Id = Guid.NewGuid(),
            TokenHash = InviteTokens.Hash(token),
            Role = Roles.Member,
            Label = UserNames.Clean(label),
            CreatedByUserId = createdBy,
            CreatedAt = now,
            ExpiresAt = now + Invite.Lifetime,
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        return (invite, token);
    }

    /// <summary>The invite behind a raw token, only while it is unconsumed and unexpired. Null for every other case.</summary>
    public async Task<Invite?> FindUsableAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > InviteTokens.MaxLength)
        {
            return null;
        }

        var hash = InviteTokens.Hash(token);
        var now = clock.GetUtcNow();
        return await db.Invites.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash && i.ConsumedAt == null && i.ExpiresAt > now, ct);
    }

    /// <summary>
    /// Marks the invite used by <paramref name="userId"/>. The check and the write are one statement, so of two
    /// concurrent registrations on the same link exactly one gets true.
    /// </summary>
    public async Task<bool> TryConsumeAsync(Guid inviteId, Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Invites
            .Where(i => i.Id == inviteId && i.ConsumedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ConsumedAt, now)
                .SetProperty(i => i.ConsumedByUserId, userId), ct);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Invite>> ListPendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Invites.AsNoTracking()
            .Where(i => i.ConsumedAt == null && i.ExpiresAt > now && i.CreatedByUserId != null)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Invites.Where(i => i.Id == id && i.ConsumedAt == null).ExecuteDeleteAsync(ct);
        return rows == 1;
    }

    /// <summary>Deletes invites that expired or were used more than a week ago.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - PurgeAfter;
        return await db.Invites
            .Where(i => i.ExpiresAt < cutoff || (i.ConsumedAt != null && i.ConsumedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }
}
