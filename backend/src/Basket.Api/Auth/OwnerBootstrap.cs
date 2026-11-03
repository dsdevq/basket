using Basket.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Basket.Api.Auth;

/// <summary>
/// First-run setup: makes sure the roles exist and, while the app has no user at all, that a registration link
/// for the owner can be opened. The link's token comes from the environment, so nothing secret is in git.
/// </summary>
public sealed class OwnerBootstrap(
    IServiceScopeFactory scopes,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<OwnerBootstrap> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                (await roles.CreateAsync(new AppRole(role))).ThrowIfFailed($"create role {role}");
            }
        }

        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var token = options.Value.BootstrapToken;
        if (string.IsNullOrEmpty(token) || token.Length < InviteTokens.MinBootstrapLength)
        {
            log.LogWarning(
                "No user exists and Auth:BootstrapToken is missing or shorter than {Min} characters; nobody can register as owner",
                InviteTokens.MinBootstrapLength);
            return;
        }

        var now = clock.GetUtcNow();
        var hash = InviteTokens.Hash(token);
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null)
        {
            db.Invites.Add(new Invite
            {
                Id = Guid.NewGuid(),
                TokenHash = hash,
                Role = Roles.Owner,
                Label = "Owner",
                CreatedAt = now,
                ExpiresAt = now + Invite.Lifetime,
            });
        }
        else if (invite.ConsumedAt is null)
        {
            invite.ExpiresAt = now + Invite.Lifetime;
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Owner registration is open for 24 hours at {Origin}/join#<Auth:BootstrapToken>", options.Value.PublicOrigin);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

internal static class IdentityResultExtensions
{
    public static void ThrowIfFailed(this IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not {action}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
