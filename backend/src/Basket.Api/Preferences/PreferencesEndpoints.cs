using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Basket.Api.Preferences;

/// <summary>
/// A person's appearance as the app shell stores it: the choices only. Absent seed or intensity means the app's own seed and the
/// tokens engine's default intensity.
/// </summary>
public sealed record AppearanceDto(
    string? Theme,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Seed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Intensity = null);

/// <summary>Body of <c>GET</c> and <c>PATCH /api/me/preferences</c>: always every field.</summary>
public sealed record PreferencesResponse(AppearanceDto Appearance);

/// <summary>
/// Body of <c>PATCH /api/me/preferences</c>. A present field replaces the stored one wholesale (an <c>appearance</c> is written as a
/// whole, so an omitted seed clears it); an omitted field is untouched. Not a deep merge.
/// </summary>
public sealed record UpdatePreferencesRequest(AppearanceDto? Appearance);

/// <summary>
/// The signed-in person's own preferences, in the lifekit app-shell shape (APP-SHELL-STANDARD section 3), the same resource
/// finance-sentry serves. Each person has their own copy, so one person's colour never reaches the other. The design preset is not
/// here: the library keeps it per device.
/// </summary>
public static partial class PreferencesEndpoints
{
    public static void MapPreferencesEndpoints(this IEndpointRouteBuilder api)
    {
        var preferences = api.MapGroup("/me/preferences").AddEndpointFilter(NoStore);
        preferences.MapGet("/", Get);
        preferences.MapMethods("/", ["PATCH"], Update);
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static async Task<IResult> Get(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var stored = await db.UserPreferences.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == UserId(principal), ct);
        return Results.Ok(ToResponse(stored));
    }

    private static async Task<IResult> Update(
        UpdatePreferencesRequest? request, ClaimsPrincipal principal, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(principal);
        if (request?.Appearance is not { } appearance)
        {
            return await Get(principal, db, ct);
        }

        if (!ThemeModes.IsKnown(appearance.Theme))
        {
            return Refused("theme_invalid", "Theme must be system, light or dark.");
        }

        if (appearance.Seed is not null && !HexColour().IsMatch(appearance.Seed))
        {
            return Refused("seed_invalid", "Seed must be a #rrggbb colour.");
        }

        if (appearance.Intensity is not null and not (>= 0d and <= 1d))
        {
            return Refused("intensity_invalid", "Intensity must be between 0 and 1.");
        }

        var seed = appearance.Seed?.ToLowerInvariant();
        var now = clock.GetUtcNow();

        // Written as a whole, in one statement; the row is created by the first save. Two first saves meeting on the primary
        // key is the only conflict, and the loser just updates the row the winner made.
        var changed = await Write(db, userId, appearance.Theme!, seed, appearance.Intensity, now, ct);
        if (changed == 0)
        {
            db.UserPreferences.Add(new UserPreference
            {
                UserId = userId,
                Theme = appearance.Theme!,
                ThemeSeed = seed,
                ThemeIntensity = appearance.Intensity,
                UpdatedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                await Write(db, userId, appearance.Theme!, seed, appearance.Intensity, now, ct);
            }
        }

        return await Get(principal, db, ct);
    }

    private static Task<int> Write(
        AppDbContext db, Guid userId, string theme, string? seed, double? intensity, DateTimeOffset now, CancellationToken ct) =>
        db.UserPreferences.Where(p => p.UserId == userId).ExecuteUpdateAsync(
            set => set
                .SetProperty(p => p.Theme, theme)
                .SetProperty(p => p.ThemeSeed, seed)
                .SetProperty(p => p.ThemeIntensity, intensity)
                .SetProperty(p => p.UpdatedAt, now),
            ct);

    /// <summary>Every field, with the defaults for a person who has saved nothing. A stored mode this build does not know reads as system.</summary>
    private static PreferencesResponse ToResponse(UserPreference? stored) =>
        new(stored is null
            ? new AppearanceDto(ThemeModes.System)
            : new AppearanceDto(ThemeModes.IsKnown(stored.Theme) ? stored.Theme : ThemeModes.System, stored.ThemeSeed, stored.ThemeIntensity));

    private static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult Refused(string error, string message) => Results.Json(new { error, message }, statusCode: 400);

    [GeneratedRegex("^#[0-9a-fA-F]{6}\\z")]
    private static partial Regex HexColour();
}
