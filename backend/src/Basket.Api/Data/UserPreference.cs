namespace Basket.Api.Data;

/// <summary>
/// One person's account-tier preferences (the lifekit app-shell standard's <c>/api/me/preferences</c> resource). Today that is the
/// appearance only: the choices, never the colours derived from them. The row appears with the first save; a person without one
/// reads as the defaults.
/// </summary>
public sealed class UserPreference
{
    public const int MaxThemeLength = 8;
    public const int SeedLength = 7;

    public Guid UserId { get; set; }

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = ThemeModes.System;

    /// <summary>The seed colour as <c>#rrggbb</c>, lower case. Null means the app's own seed.</summary>
    public string? ThemeSeed { get; set; }

    /// <summary>Palette intensity, 0 to 1. Null means the engine's default.</summary>
    public double? ThemeIntensity { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class ThemeModes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static bool IsKnown(string? mode) => mode is System or Light or Dark;
}
