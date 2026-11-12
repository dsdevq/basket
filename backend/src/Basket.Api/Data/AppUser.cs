using Microsoft.AspNetCore.Identity;

namespace Basket.Api.Data;

/// <summary>
/// A person who can sign in. There is no password. A passkey is a credential, and so is a login with the org identity
/// provider (a <c>user_logins</c> row), which also brings the email.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppRole : IdentityRole<Guid>
{
    public AppRole()
    {
    }

    public AppRole(string name) : base(name)
    {
    }
}

public static class Roles
{
    public const string Owner = "owner";
    public const string Member = "member";

    public static readonly string[] All = [Owner, Member];
}
