using Basket.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Auth;

/// <summary>What the org identity provider vouched for: the stable subject, the email it holds and whether it checked it, the name, and the app role.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, bool EmailVerified, string? Name, bool HasAppRole);

/// <summary>Why a provider sign-in did not end in an account.</summary>
public enum ExternalLoginRefusal
{
    /// <summary>No account, no app role, an unverified email, or an account that cannot take this subject.</summary>
    AccessDenied,

    /// <summary>
    /// The person holds the app role and matches no account, but some account still has no provider login. Creating a Member now could
    /// duplicate that account's owner, so the person is sent to sign in with the old method first (see <see cref="ExternalLoginService"/>).
    /// </summary>
    LinkRequired,
}

/// <summary>The decision for one provider sign-in: the account to open a session for, or why there is none.</summary>
public sealed record ExternalLoginResult(AppUser? User, ExternalLoginRefusal Refusal = ExternalLoginRefusal.AccessDenied)
{
    public static ExternalLoginResult SignedIn(AppUser user) => new(user);

    public static ExternalLoginResult Denied { get; } = new(User: null);

    public static ExternalLoginResult LinkRequired { get; } = new(User: null, Refusal: ExternalLoginRefusal.LinkRequired);
}

/// <summary>
/// Turns a provider identity into a basket account. A linked subject signs in. Otherwise a person who also holds a valid basket
/// session for an account with no provider login links that account (the passkey accounts that predate the provider; the session
/// is the proof of whose they are). Otherwise a verified email that matches an account links to it. Otherwise holding the app role
/// creates a Member, but only while every account already has a provider login: while one has none, the person is told to sign in
/// with the old method first, so a legacy account is never duplicated. Anything else is refused.
/// An unverified email never links and never creates, and an account already linked to another subject is never taken over.
/// </summary>
public sealed class ExternalLoginService(
    AppDbContext db,
    UserManager<AppUser> users,
    TimeProvider clock,
    ILogger<ExternalLoginService> log)
{
    /// <param name="identity">What the provider vouched for.</param>
    /// <param name="session">The account of the basket session the request carried, if it carried a valid one.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ExternalLoginResult> ResolveAsync(ExternalIdentity identity, AppUser? session, CancellationToken ct)
    {
        var linked = await users.FindByLoginAsync(OidcLoginOptions.Scheme, identity.Subject);
        if (linked is not null)
        {
            await RememberEmailAsync(linked, identity);
            return await IsUsableAsync(linked) ? ExternalLoginResult.SignedIn(linked) : ExternalLoginResult.Denied;
        }

        if (session is not null && await TryLinkSessionAccountAsync(session, identity))
        {
            return ExternalLoginResult.SignedIn(session);
        }

        var email = identity.Email?.Trim();
        if (!identity.EmailVerified || string.IsNullOrEmpty(email))
        {
            return ExternalLoginResult.Denied;
        }

        var normalized = users.NormalizeEmail(email);
        var matches = await db.Users.Where(u => u.NormalizedEmail == normalized).Take(2).ToListAsync(ct);
        if (matches.Count > 1)
        {
            // Two accounts claim the address: guessing which one is hers is how a sign-in lands in the wrong list.
            log.LogWarning("More than one basket account holds the verified email of sign-in subject {Subject}; refused", identity.Subject);
            return ExternalLoginResult.Denied;
        }

        if (matches.Count == 1)
        {
            var existing = matches[0];
            var already = await users.GetLoginsAsync(existing);
            if (already.Any(l => l.LoginProvider == OidcLoginOptions.Scheme) || !await IsUsableAsync(existing))
            {
                return ExternalLoginResult.Denied;
            }

            var linkedNow = await users.AddLoginAsync(existing, Login(identity));
            if (!linkedNow.Succeeded)
            {
                return ExternalLoginResult.Denied;
            }

            log.LogInformation("Linked account {UserId} to its identity provider login", existing.Id);
            return ExternalLoginResult.SignedIn(existing);
        }

        if (!identity.HasAppRole)
        {
            return ExternalLoginResult.Denied;
        }

        if (await HasAccountWithoutProviderLoginAsync(ct))
        {
            // A legacy account may be this very person's: only its own session can link it, so nothing is created beside it.
            log.LogInformation("Sign-in subject {Subject} matches no account while some account has no provider login; asked to link first", identity.Subject);
            return ExternalLoginResult.LinkRequired;
        }

        var created = await CreateMemberAsync(identity, email, ct);
        return created is null ? ExternalLoginResult.Denied : ExternalLoginResult.SignedIn(created);
    }

    /// <summary>
    /// The valid session's account takes this subject when it has no provider login yet. An account that already has one never takes
    /// a second (the subject then falls through and is judged as if there were no session), and a locked-out account takes none.
    /// </summary>
    private async Task<bool> TryLinkSessionAccountAsync(AppUser session, ExternalIdentity identity)
    {
        if (!await IsUsableAsync(session)
            || (await users.GetLoginsAsync(session)).Any(l => l.LoginProvider == OidcLoginOptions.Scheme)
            || !(await users.AddLoginAsync(session, Login(identity))).Succeeded)
        {
            return false;
        }

        await RememberEmailAsync(session, identity);
        log.LogInformation("Linked account {UserId} to its identity provider login through its own session", session.Id);
        return true;
    }

    private Task<bool> HasAccountWithoutProviderLoginAsync(CancellationToken ct) =>
        db.Users.AnyAsync(u => !db.UserLogins.Any(l => l.UserId == u.Id && l.LoginProvider == OidcLoginOptions.Scheme), ct);

    private async Task<AppUser?> CreateMemberAsync(ExternalIdentity identity, string email, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var user = new AppUser
        {
            Id = id,
            UserName = id.ToString("N"),
            Email = email,
            EmailConfirmed = true,
            DisplayName = UserNames.Clean(identity.Name) ?? UserNames.Clean(email.Split('@')[0]) ?? "Member",
            CreatedAt = clock.GetUtcNow(),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!(await users.CreateAsync(user)).Succeeded
            || !(await users.AddToRoleAsync(user, Roles.Member)).Succeeded
            || !(await users.AddLoginAsync(user, Login(identity))).Succeeded)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await tx.CommitAsync(ct);
        log.LogInformation("Created member {UserId} from an identity provider login", id);
        return user;
    }

    /// <summary>A linked account without an email takes the provider's verified one, so the session can show it. It never replaces one.</summary>
    private async Task RememberEmailAsync(AppUser user, ExternalIdentity identity)
    {
        var email = identity.Email?.Trim();
        if (identity.EmailVerified && !string.IsNullOrEmpty(email) && string.IsNullOrEmpty(user.Email))
        {
            await users.SetEmailAsync(user, email);
        }
    }

    private async Task<bool> IsUsableAsync(AppUser user) => !await users.IsLockedOutAsync(user);

    private static UserLoginInfo Login(ExternalIdentity identity) =>
        new(OidcLoginOptions.Scheme, identity.Subject, OidcLoginOptions.Scheme);
}
