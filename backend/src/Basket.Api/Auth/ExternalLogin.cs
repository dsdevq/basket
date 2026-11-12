using Basket.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Auth;

/// <summary>What the org identity provider vouched for: the stable subject, the email it holds and whether it checked it, the name, and the app role.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, bool EmailVerified, string? Name, bool HasAppRole);

/// <summary>
/// Turns a provider identity into a basket account. A linked subject signs in. Otherwise a verified email that matches an
/// account links to it. Otherwise holding the app role creates a Member. Anything else is refused (null).
/// An unverified email never links and never creates, and an account already linked to another subject is never taken over.
/// </summary>
public sealed class ExternalLoginService(
    AppDbContext db,
    UserManager<AppUser> users,
    TimeProvider clock,
    ILogger<ExternalLoginService> log)
{
    public async Task<AppUser?> ResolveAsync(ExternalIdentity identity, CancellationToken ct)
    {
        var linked = await users.FindByLoginAsync(OidcLoginOptions.Scheme, identity.Subject);
        if (linked is not null)
        {
            await RememberEmailAsync(linked, identity);
            return await IsUsableAsync(linked) ? linked : null;
        }

        var email = identity.Email?.Trim();
        if (!identity.EmailVerified || string.IsNullOrEmpty(email))
        {
            return null;
        }

        var normalized = users.NormalizeEmail(email);
        var matches = await db.Users.Where(u => u.NormalizedEmail == normalized).Take(2).ToListAsync(ct);
        if (matches.Count > 1)
        {
            // Two accounts claim the address: guessing which one is hers is how a sign-in lands in the wrong list.
            log.LogWarning("More than one basket account holds the verified email of sign-in subject {Subject}; refused", identity.Subject);
            return null;
        }

        if (matches.Count == 1)
        {
            var existing = matches[0];
            var already = await users.GetLoginsAsync(existing);
            if (already.Any(l => l.LoginProvider == OidcLoginOptions.Scheme) || !await IsUsableAsync(existing))
            {
                return null;
            }

            var linkedNow = await users.AddLoginAsync(existing, Login(identity));
            if (!linkedNow.Succeeded)
            {
                return null;
            }

            log.LogInformation("Linked account {UserId} to its identity provider login", existing.Id);
            return existing;
        }

        return identity.HasAppRole ? await CreateMemberAsync(identity, email, ct) : null;
    }

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
