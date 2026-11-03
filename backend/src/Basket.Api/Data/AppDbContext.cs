using System.Text;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Basket.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<ListItem> ListItems => Set<ListItem>();

    public DbSet<Invite> Invites => Set<Invite>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(UserNames.MaxLength).IsRequired();
        });

        builder.Entity<ListItem>(item =>
        {
            item.ToTable("list_item");
            item.HasKey(i => i.Id);
            item.Property(i => i.Text).HasMaxLength(ListItem.MaxTextLength).IsRequired();
            item.HasOne<AppUser>().WithMany().HasForeignKey(i => i.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CheckedByUserId).OnDelete(DeleteBehavior.SetNull);
            item.HasIndex(i => new { i.IsChecked, i.AddedAt });
        });

        builder.Entity<Invite>(invite =>
        {
            invite.ToTable("invite");
            invite.HasKey(i => i.Id);
            invite.Property(i => i.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            invite.Property(i => i.Role).HasMaxLength(16).IsRequired();
            invite.Property(i => i.Label).HasMaxLength(UserNames.MaxLength);
            invite.HasIndex(i => i.TokenHash).IsUnique();
            invite.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
            invite.HasOne<AppUser>().WithMany().HasForeignKey(i => i.ConsumedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<AppRole>().ToTable("roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserPasskey<Guid>>().ToTable("user_passkeys");

        SnakeCaseColumns(builder);
    }

    /// <summary>Column names are snake_case, to match the table names, without another package.</summary>
    private static void SnakeCaseColumns(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            // Properties inside a JSON-mapped type (the passkey data) are named in the document, not as columns.
            if (entity.IsMappedToJson())
            {
                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && !char.IsUpper(name[i - 1]);
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && (prevLower || (nextLower && char.IsUpper(name[i - 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

public static class UserNames
{
    public const int MaxLength = 40;

    /// <summary>Trims, collapses whitespace, drops control characters. Null when nothing usable is left.</summary>
    public static string? Clean(string? value, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        if (sb.Length == 0)
        {
            return null;
        }

        return sb.Length > maxLength ? sb.ToString(0, maxLength).TrimEnd() : sb.ToString();
    }
}
