using System.Security.Claims;
using Basket.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Basket.Api.Auth;

public sealed record InviteRequest(string? Token);

public sealed record RegisterOptionsRequest(string? Token, string? DisplayName);

public sealed record RegisterRequest(string? Token, string? CredentialJson);

public sealed record SignInRequest(string? CredentialJson);

public sealed record CreateInviteRequest(string? Label);

public sealed record SessionUser(Guid Id, string DisplayName, string Role, string? Email = null);

public sealed record SessionResponse(bool Authenticated, SessionUser? User);

public sealed record InviteResponse(Guid Id, string? Label, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record CreatedInviteResponse(Guid Id, string? Label, DateTimeOffset ExpiresAt, string Token, string Link);

public sealed record MemberResponse(Guid Id, string DisplayName, string Role, DateTimeOffset CreatedAt);

public static class AuthEndpoints
{
    public const string AuthLimiter = "auth";
    public const string InviteLimiter = "invite";

    /// <summary>One answer for every way a link can be unusable, so a probe learns nothing.</summary>
    private static IResult InviteInvalid() =>
        Results.Json(new { error = "invite_invalid", message = "This invite link is not valid any more." }, statusCode: 404);

    private static IResult PasskeyFailed() =>
        Results.Json(new { error = "passkey_failed", message = "That passkey could not be used." }, statusCode: 400);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/session", Session).AllowAnonymous();

        var auth = api.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(AuthLimiter);
        auth.MapPost("/invite", CheckInvite);
        auth.MapPost("/register/options", RegisterOptions);
        auth.MapPost("/register", Register);
        auth.MapPost("/passkey-failure", PasskeyFailureReport.Handle);
        auth.MapPost("/sign-in/options", SignInOptions);
        auth.MapPost("/sign-in", SignIn);
        auth.MapOidcEndpoints();
        api.MapPost("/auth/sign-out", SignOut).RequireAuthorization();

        var owner = api.MapGroup("/invites").RequireAuthorization(Policies.Owner);
        owner.MapGet("/", ListInvites);
        owner.MapPost("/", CreateInvite).RequireRateLimiting(InviteLimiter);
        owner.MapDelete("/{id:guid}", RevokeInvite);

        api.MapGet("/members", ListMembers).RequireAuthorization(Policies.Owner);
    }

    private static IResult Session(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Results.Ok(new SessionResponse(false, null));
        }

        return Results.Ok(new SessionResponse(true, ToSessionUser(principal)));
    }

    internal static SessionUser ToSessionUser(ClaimsPrincipal principal) => new(
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
        principal.FindFirstValue(Policies.DisplayNameClaim) ?? string.Empty,
        principal.IsInRole(Roles.Owner) ? Roles.Owner : Roles.Member,
        principal.FindFirstValue(ClaimTypes.Email));

    private static async Task<IResult> CheckInvite(InviteRequest request, InviteService invites, CancellationToken ct)
    {
        var invite = await invites.FindUsableAsync(request.Token, ct);
        return invite is null ? InviteInvalid() : Results.Ok(new { role = invite.Role, label = invite.Label });
    }

    private static async Task<IResult> RegisterOptions(
        RegisterOptionsRequest request,
        InviteService invites,
        SignInManager<AppUser> signIn,
        CancellationToken ct)
    {
        var invite = await invites.FindUsableAsync(request.Token, ct);
        if (invite is null)
        {
            return InviteInvalid();
        }

        var displayName = UserNames.Clean(request.DisplayName);
        if (displayName is null)
        {
            return Results.Json(new { error = "name_required", message = "Enter the name to show next to your items." }, statusCode: 400);
        }

        // The id is chosen here and travels in the signed attestation state, so the account created on the
        // next request is the one this ceremony was for.
        var userId = Guid.NewGuid().ToString();
        var optionsJson = await signIn.MakePasskeyCreationOptionsAsync(new()
        {
            Id = userId,
            Name = displayName,
            DisplayName = displayName,
        });
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        InviteService invites,
        AppDbContext db,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        TimeProvider clock,
        ILogger<InviteService> log,
        CancellationToken ct)
    {
        var invite = await invites.FindUsableAsync(request.Token, ct);
        if (invite is null)
        {
            return InviteInvalid();
        }

        if (string.IsNullOrEmpty(request.CredentialJson))
        {
            return PasskeyFailed();
        }

        var attestation = await signIn.PerformPasskeyAttestationAsync(request.CredentialJson);
        if (!attestation.Succeeded || !Guid.TryParse(attestation.UserEntity.Id, out var userId))
        {
            return PasskeyFailed();
        }

        var user = new AppUser
        {
            Id = userId,
            UserName = userId.ToString("N"),
            DisplayName = UserNames.Clean(attestation.UserEntity.DisplayName) ?? "Member",
            CreatedAt = clock.GetUtcNow(),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ownerTaken = invite.Role == Roles.Owner && (await users.GetUsersInRoleAsync(Roles.Owner)).Count > 0;
        if (ownerTaken
            || !(await users.CreateAsync(user)).Succeeded
            || !(await users.AddToRoleAsync(user, invite.Role)).Succeeded
            || !(await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey)).Succeeded
            || !await invites.TryConsumeAsync(invite.Id, userId, ct))
        {
            await tx.RollbackAsync(ct);
            return InviteInvalid();
        }

        await tx.CommitAsync(ct);
        await signIn.SignInAsync(user, isPersistent: true);
        log.LogInformation("Registered {Role} {UserId} with invite {InviteId}", invite.Role, userId, invite.Id);
        return Results.Ok(new SessionResponse(true, new SessionUser(user.Id, user.DisplayName, invite.Role)));
    }

    private static async Task<IResult> SignInOptions(SignInManager<AppUser> signIn)
    {
        // Discoverable credentials: no username is asked for, so there is nothing to enumerate.
        var optionsJson = await signIn.MakePasskeyRequestOptionsAsync(user: null);
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> SignIn(SignInRequest request, SignInManager<AppUser> signIn, UserManager<AppUser> users)
    {
        if (string.IsNullOrEmpty(request.CredentialJson))
        {
            return PasskeyFailed();
        }

        Microsoft.AspNetCore.Identity.SignInResult result;
        try
        {
            result = await signIn.PasskeySignInAsync(request.CredentialJson);
        }
        catch (InvalidOperationException)
        {
            // No sign-in ceremony was started from this browser: the same answer as a wrong passkey.
            result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
        }

        if (!result.Succeeded)
        {
            return Results.Json(new { error = "sign_in_failed", message = "That passkey is not recognised here." }, statusCode: 401);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> SignOut(
        HttpContext http,
        SignInManager<AppUser> signIn,
        IOptions<OidcLoginOptions> oidc,
        IDataProtectionProvider protection,
        ILogger<ExternalLoginService> log)
    {
        await signIn.SignOutAsync();

        // A session that came from the org sign-in ends there too; otherwise the next sign-in would re-enter silently.
        var provider = await OidcEndpoints.EndProviderSessionAsync(http, oidc.Value, protection, log);
        return provider is null ? Results.NoContent() : Results.Ok(provider);
    }

    private static async Task<IResult> ListInvites(InviteService invites, CancellationToken ct)
    {
        var pending = await invites.ListPendingAsync(ct);
        return Results.Ok(pending.Select(i => new InviteResponse(i.Id, i.Label, i.CreatedAt, i.ExpiresAt)));
    }

    private static async Task<IResult> CreateInvite(
        CreateInviteRequest request,
        ClaimsPrincipal principal,
        InviteService invites,
        IOptions<AuthOptions> options,
        CancellationToken ct)
    {
        var createdBy = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var (invite, token) = await invites.CreateAsync(createdBy, request.Label, ct);
        // The token rides in the URL fragment: browsers never send it to the server, so it stays out of access logs.
        var link = $"{options.Value.PublicOrigin}/join#{token}";
        return Results.Created($"/api/invites/{invite.Id}", new CreatedInviteResponse(invite.Id, invite.Label, invite.ExpiresAt, token, link));
    }

    private static async Task<IResult> RevokeInvite(Guid id, InviteService invites, CancellationToken ct) =>
        await invites.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> ListMembers(AppDbContext db, CancellationToken ct)
    {
        var members = await (
            from u in db.Users.AsNoTracking()
            join ur in db.UserRoles on u.Id equals ur.UserId
            join r in db.Roles on ur.RoleId equals r.Id
            orderby u.CreatedAt
            select new MemberResponse(u.Id, u.DisplayName, r.Name!, u.CreatedAt)).ToListAsync(ct);
        return Results.Ok(members);
    }
}

public static class Policies
{
    public const string Owner = "owner";
    public const string CatalogRead = "catalog-read";
    public const string CatalogAdd = "catalog-add";
    public const string DisplayNameClaim = "display_name";
}
