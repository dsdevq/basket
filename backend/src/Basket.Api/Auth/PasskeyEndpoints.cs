using System.Security.Claims;
using Basket.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Auth;

public sealed record AddPasskeyRequest(string? CredentialJson, string? Name);

/// <summary>One registered passkey. The id is the credential id, base64url, and is all the client needs to remove it.</summary>
public sealed record PasskeyResponse(string Id, string Name, DateTimeOffset CreatedAt, bool BackedUp);

/// <summary>
/// A signed-in person's own passkeys: list them, add another (a new phone), remove one - never the last.
/// Everything is scoped to the caller; there is no way to name another person's passkey.
/// </summary>
public static class PasskeyEndpoints
{
    public const int MaxNameLength = 40;
    public const string DefaultName = "Passkey";

    /// <summary>A guard against a runaway client, far above what two people reach.</summary>
    public const int MaxPasskeys = 10;

    private static IResult PasskeyFailed() =>
        Results.Json(new { error = "passkey_failed", message = "That passkey could not be added." }, statusCode: 400);

    public static void MapPasskeyEndpoints(this IEndpointRouteBuilder api)
    {
        var passkeys = api.MapGroup("/passkeys").RequireAuthorization();
        passkeys.MapGet("/", List);
        passkeys.MapPost("/options", Options).RequireRateLimiting(AuthEndpoints.AuthLimiter);
        passkeys.MapPost("/", Add).RequireRateLimiting(AuthEndpoints.AuthLimiter);
        passkeys.MapDelete("/{id}", Remove);
    }

    private static async Task<IResult> List(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok((await users.GetPasskeysAsync(user)).OrderBy(p => p.CreatedAt).Select(ToResponse));
    }

    private static async Task<IResult> Options(ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if ((await users.GetPasskeysAsync(user)).Count >= MaxPasskeys)
        {
            return TooMany();
        }

        // The person's own id: the ceremony adds a credential to this account, and Identity lists the
        // passkeys it already has as excluded, so the same device is not registered twice.
        var optionsJson = await signIn.MakePasskeyCreationOptionsAsync(new()
        {
            Id = user.Id.ToString(),
            Name = user.DisplayName,
            DisplayName = user.DisplayName,
        });
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> Add(
        AddPasskeyRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        ILogger<AddPasskeyRequest> log)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrEmpty(request.CredentialJson))
        {
            return PasskeyFailed();
        }

        if ((await users.GetPasskeysAsync(user)).Count >= MaxPasskeys)
        {
            return TooMany();
        }

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await signIn.PerformPasskeyAttestationAsync(request.CredentialJson);
        }
        catch (InvalidOperationException)
        {
            // No ceremony was started from this browser: the same answer as a wrong passkey.
            return PasskeyFailed();
        }

        // The signed ceremony state names the account it was started for; it must be the caller's.
        if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id.ToString())
        {
            return PasskeyFailed();
        }

        attestation.Passkey.Name = UserNames.Clean(request.Name, MaxNameLength) ?? DefaultName;
        if (!(await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey)).Succeeded)
        {
            return PasskeyFailed();
        }

        log.LogInformation("User {UserId} added a passkey", user.Id);
        return Results.Created($"/api/passkeys/{Encode(attestation.Passkey.CredentialId)}", ToResponse(attestation.Passkey));
    }

    private static async Task<IResult> Remove(
        string id,
        ClaimsPrincipal principal,
        AppDbContext db,
        UserManager<AppUser> users,
        ILogger<AddPasskeyRequest> log,
        CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        byte[] credentialId;
        try
        {
            credentialId = WebEncoders.Base64UrlDecode(id);
        }
        catch (FormatException)
        {
            return Results.NotFound();
        }

        // Lock the person's row for the count-then-delete, so two removals sent together cannot both
        // see "two left" and leave an account nobody can sign in to.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM users WHERE id = {user.Id} FOR UPDATE", ct);

        var all = await users.GetPasskeysAsync(user);
        if (!all.Any(p => p.CredentialId.AsSpan().SequenceEqual(credentialId)))
        {
            return Results.NotFound();
        }

        if (all.Count <= 1)
        {
            return Results.Json(
                new { error = "last_passkey", message = "That is your only passkey. Add another one before removing it." },
                statusCode: 409);
        }

        if (!(await users.RemovePasskeyAsync(user, credentialId)).Succeeded)
        {
            return Results.Problem(statusCode: 500);
        }

        await tx.CommitAsync(ct);
        log.LogInformation("User {UserId} removed a passkey", user.Id);
        return Results.NoContent();
    }

    private static IResult TooMany() =>
        Results.Json(new { error = "too_many_passkeys", message = $"You can have up to {MaxPasskeys} passkeys." }, statusCode: 409);

    private static string Encode(byte[] credentialId) => WebEncoders.Base64UrlEncode(credentialId);

    private static PasskeyResponse ToResponse(UserPasskeyInfo passkey) =>
        new(Encode(passkey.CredentialId), string.IsNullOrWhiteSpace(passkey.Name) ? DefaultName : passkey.Name, passkey.CreatedAt, passkey.IsBackedUp);
}
