using Basket.Api.Auth;
using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Ingest;

public static class IngestTokens
{
    public const string Prefix = "bsk_";
    public const int MaxLength = 128;

    /// <summary>256 random bits, base64url, behind a fixed prefix so a leaked one is recognisable.</summary>
    public static string Generate() => Prefix + InviteTokens.Generate();

    public static string Hash(string token) => InviteTokens.Hash(token);

    /// <summary>The token in an <c>Authorization: Bearer</c> header, or null when there is none.</summary>
    public static string? FromHeader(string? header)
    {
        const string scheme = "Bearer ";
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[scheme.Length..].Trim();
        return token.Length is > 0 and <= MaxLength ? token : null;
    }
}

/// <summary>Issue, list, check and revoke the tokens the PC-side price reader signs its posts with.</summary>
public sealed class IngestTokenService(AppDbContext db, TimeProvider clock)
{
    /// <summary>More than a household has readers; a guard against a runaway client.</summary>
    public const int MaxTokens = 5;

    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    /// <summary>Mints a token. Null when the owner already holds <see cref="MaxTokens"/>.</summary>
    public async Task<(IngestToken Token, string Raw)?> CreateAsync(Guid createdBy, string? label, CancellationToken ct)
    {
        if (await db.IngestTokens.CountAsync(ct) >= MaxTokens)
        {
            return null;
        }

        var raw = IngestTokens.Generate();
        var token = new IngestToken
        {
            Id = Guid.NewGuid(),
            TokenHash = IngestTokens.Hash(raw),
            Label = UserNames.Clean(label),
            CreatedByUserId = createdBy,
            CreatedAt = clock.GetUtcNow(),
        };
        db.IngestTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return (token, raw);
    }

    public async Task<IReadOnlyList<IngestToken>> ListAsync(CancellationToken ct) =>
        await db.IngestTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct) =>
        await db.IngestTokens.Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 1;

    /// <summary>True when <paramref name="raw"/> is a live token. Remembers that it was used.</summary>
    public async Task<bool> AuthenticateAsync(string? raw, CancellationToken ct)
    {
        if (raw is null)
        {
            return false;
        }

        var hash = IngestTokens.Hash(raw);
        var token = await db.IngestTokens.AsNoTracking().Where(t => t.TokenHash == hash).Select(t => new { t.Id, t.LastUsedAt }).FirstOrDefaultAsync(ct);
        if (token is null)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        if (token.LastUsedAt is null || now - token.LastUsedAt >= TouchEvery)
        {
            await db.IngestTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        return true;
    }
}
