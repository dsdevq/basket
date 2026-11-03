using Microsoft.AspNetCore.Identity;

namespace Basket.Api.Data;

/// <summary>A person who can sign in. There is no password and no email: a passkey is the only credential.</summary>
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
