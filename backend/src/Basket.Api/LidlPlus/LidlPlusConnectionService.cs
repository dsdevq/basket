using Basket.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.LidlPlus;

/// <summary>What a pasted refresh token has to look like before basket spends a request on it.</summary>
public static class LidlPlusTokens
{
    public const int MinLength = 16;
    public const int MaxLength = 1024;

    /// <summary>
    /// The token as it will be sent, or null when <paramref name="raw"/> cannot be one. Surrounding spaces and the quotes a
    /// copy from a terminal or a JSON file brings are dropped; a token holds no spaces or control characters inside.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var token = raw.Trim().Trim('"', '\'', '`').Trim();
        return token.Length is >= MinLength and <= MaxLength && token.All(c => c is > ' ' and <= '~') ? token : null;
    }
}

/// <summary>Encrypts and decrypts the refresh token with the app's Data Protection keys (the same ring as the cookie and the cards).</summary>
public sealed class LidlPlusTokenProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("basket.lidl-plus.refresh-token.v1");

    public string Protect(string token) => _protector.Protect(token);

    /// <summary>The token, or null when the stored value cannot be read (keys lost or replaced).</summary>
    public string? Unprotect(string stored)
    {
        try
        {
            return _protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Where the connection stands. <see cref="Connected"/> is the only state in which basket can read Lidl Plus.</summary>
public enum LidlPlusState
{
    NotConnected,
    Connected,

    /// <summary>Lidl refused the stored token the last time it was used. A new paste fixes it.</summary>
    NeedsNewToken,

    /// <summary>The stored token cannot be decrypted (the key ring was lost). A new paste fixes it.</summary>
    Unreadable,
}

public sealed record LidlPlusStatus(LidlPlusState State, DateTimeOffset? ConnectedAt, DateTimeOffset? VerifiedAt)
{
    public static LidlPlusStatus None { get; } = new(LidlPlusState.NotConnected, null, null);
}

public enum ConnectOutcome
{
    /// <summary>Lidl accepted the token and it is stored.</summary>
    Connected,

    /// <summary>Lidl refused the token. Nothing was stored and an existing connection was left as it was.</summary>
    Rejected,

    /// <summary>Lidl could not be reached. Nothing was stored.</summary>
    Unavailable,
}

/// <summary>An access token for one call to Lidl, or the reason there is none. The access token is a secret: do not log it.</summary>
public sealed record LidlAccess(LidlPlusState State, string? AccessToken)
{
    public override string ToString() => $"LidlAccess({State})";
}

/// <summary>
/// Pasting a refresh token makes a connection; asking for an access token keeps it alive. Lidl rotates the refresh token
/// whenever it is used, so the replacement is saved before anything else happens: losing it would end the connection.
/// </summary>
public sealed class LidlPlusConnectionService(AppDbContext db, LidlPlusTokenProtector protector, ILidlPlusAuth auth, TimeProvider clock)
{
    // One household, one app instance: a lock around the exchange is enough to stop two exchanges of the same token from racing,
    // which Lidl would answer by refusing the second and ending the connection.
    private static readonly SemaphoreSlim Exchange = new(1, 1);

    public async Task<LidlPlusStatus> StatusAsync(CancellationToken ct)
    {
        var row = await db.LidlPlusConnections.AsNoTracking().SingleOrDefaultAsync(ct);
        return row is null ? LidlPlusStatus.None : Describe(row);
    }

    /// <summary>Checks the pasted token with Lidl and, if Lidl accepts it, stores it (replacing any earlier one).</summary>
    public async Task<(ConnectOutcome Outcome, bool Created)> ConnectAsync(string token, CancellationToken ct)
    {
        await Exchange.WaitAsync(ct);
        try
        {
            var result = await auth.RefreshAsync(token, ct);
            if (result.Outcome != LidlAuthOutcome.Accepted)
            {
                return (result.Outcome == LidlAuthOutcome.Rejected ? ConnectOutcome.Rejected : ConnectOutcome.Unavailable, false);
            }

            var now = clock.GetUtcNow();
            var protectedToken = protector.Protect(result.RefreshToken ?? token);
            var created = !await db.LidlPlusConnections.AnyAsync(ct);

            // One statement, so two pastes in one moment cannot both insert: the second replaces the first.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO lidl_plus_connection (id, refresh_token_protected, connected_at, verified_at, problem)
                VALUES ({LidlPlusConnection.SingletonId}, {protectedToken}, {now}, {now}, NULL)
                ON CONFLICT (id) DO UPDATE SET
                    refresh_token_protected = EXCLUDED.refresh_token_protected,
                    connected_at = EXCLUDED.connected_at,
                    verified_at = EXCLUDED.verified_at,
                    problem = NULL
                """, ct);
            return (ConnectOutcome.Connected, created);
        }
        finally
        {
            Exchange.Release();
        }
    }

    /// <summary>
    /// An access token for a call to Lidl Plus, made from the stored refresh token. The rotated refresh token is saved before this
    /// returns. A refusal marks the connection as needing a new token; an unreachable Lidl changes nothing.
    /// </summary>
    public async Task<LidlAccess> AccessAsync(CancellationToken ct)
    {
        await Exchange.WaitAsync(ct);
        try
        {
            var row = await db.LidlPlusConnections.SingleOrDefaultAsync(ct);
            if (row is null)
            {
                return new LidlAccess(LidlPlusState.NotConnected, null);
            }

            if (row.Problem is not null)
            {
                // Lidl already refused this token; asking again would only be refused again.
                return new LidlAccess(LidlPlusState.NeedsNewToken, null);
            }

            var token = protector.Unprotect(row.RefreshTokenProtected);
            if (token is null)
            {
                return new LidlAccess(LidlPlusState.Unreadable, null);
            }

            var result = await auth.RefreshAsync(token, ct);
            switch (result.Outcome)
            {
                case LidlAuthOutcome.Accepted:
                    if (result.RefreshToken is not null)
                    {
                        row.RefreshTokenProtected = protector.Protect(result.RefreshToken);
                    }

                    row.VerifiedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                    return new LidlAccess(LidlPlusState.Connected, result.AccessToken);

                case LidlAuthOutcome.Rejected:
                    row.Problem = LidlPlusConnection.TokenRejected;
                    await db.SaveChangesAsync(ct);
                    return new LidlAccess(LidlPlusState.NeedsNewToken, null);

                default:
                    // Lidl could not be asked; the token may be fine, so the connection stays as it was.
                    return new LidlAccess(Describe(row).State, null);
            }
        }
        finally
        {
            Exchange.Release();
        }
    }

    public async Task<bool> DisconnectAsync(CancellationToken ct)
    {
        await Exchange.WaitAsync(ct);
        try
        {
            return await db.LidlPlusConnections.ExecuteDeleteAsync(ct) > 0;
        }
        finally
        {
            Exchange.Release();
        }
    }

    private LidlPlusStatus Describe(LidlPlusConnection row)
    {
        var state = row.Problem is not null ? LidlPlusState.NeedsNewToken
            : protector.Unprotect(row.RefreshTokenProtected) is null ? LidlPlusState.Unreadable
            : LidlPlusState.Connected;
        return new LidlPlusStatus(state, row.ConnectedAt, row.VerifiedAt);
    }
}
