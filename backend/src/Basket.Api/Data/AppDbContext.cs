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

    public DbSet<IngestToken> IngestTokens => Set<IngestToken>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<StoreListing> StoreListings => Set<StoreListing>();

    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();

    public DbSet<PricePullRun> PricePullRuns => Set<PricePullRun>();

    public DbSet<PaidPrice> PaidPrices => Set<PaidPrice>();

    public DbSet<LoyaltyCard> LoyaltyCards => Set<LoyaltyCard>();

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

        builder.Entity<IngestToken>(token =>
        {
            token.ToTable("ingest_token");
            token.HasKey(t => t.Id);
            token.Property(t => t.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            token.Property(t => t.Label).HasMaxLength(UserNames.MaxLength);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasOne<AppUser>().WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Product>(product =>
        {
            product.ToTable("product");
            product.HasKey(p => p.Id);
            product.Property(p => p.Name).HasMaxLength(Product.MaxNameLength).IsRequired();
            product.HasOne<AppUser>().WithMany().HasForeignKey(p => p.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<StoreListing>(listing =>
        {
            listing.ToTable("store_listing");
            listing.HasKey(l => l.Id);
            listing.Property(l => l.Store).HasConversion<string>().HasMaxLength(16);
            listing.Property(l => l.Url).HasMaxLength(400).IsRequired();
            listing.Property(l => l.Title).HasMaxLength(300);
            listing.Property(l => l.LastError).HasMaxLength(500);
            listing.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            listing.HasIndex(l => new { l.ProductId, l.Store }).IsUnique();
        });

        builder.Entity<PriceObservation>(observation =>
        {
            observation.ToTable("price_observation");
            observation.HasKey(o => o.Id);
            observation.Property(o => o.Price).HasPrecision(10, 2);
            observation.Property(o => o.WasPrice).HasPrecision(10, 2);
            observation.Property(o => o.LoyaltyPrice).HasPrecision(10, 2);
            observation.Property(o => o.LoyaltyScheme).HasMaxLength(32);
            observation.Property(o => o.PromoText).HasMaxLength(200);
            observation.HasOne<StoreListing>().WithMany().HasForeignKey(o => o.ListingId).OnDelete(DeleteBehavior.Cascade);
            observation.HasIndex(o => new { o.ListingId, o.ObservedOn }).IsUnique();
        });

        builder.Entity<PricePullRun>(run =>
        {
            run.ToTable("price_pull_run");
            run.HasKey(r => r.Id);
            run.Property(r => r.Store).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.Message).HasMaxLength(500);
            run.HasIndex(r => new { r.Store, r.FinishedAt });
        });

        builder.Entity<PaidPrice>(paid =>
        {
            paid.ToTable("paid_price");
            paid.HasKey(p => p.Id);
            paid.Property(p => p.Store).HasConversion<string>().HasMaxLength(16);
            paid.Property(p => p.Price).HasPrecision(10, 2);
            paid.HasOne<Product>().WithMany().HasForeignKey(p => p.ProductId).OnDelete(DeleteBehavior.Cascade);
            paid.HasIndex(p => new { p.ProductId, p.Store, p.PaidOn });
        });

        builder.Entity<LoyaltyCard>(card =>
        {
            card.ToTable("loyalty_card");
            card.HasKey(c => c.Id);
            card.Property(c => c.Store).HasConversion<string>().HasMaxLength(16);
            card.Property(c => c.Format).HasConversion<string>().HasMaxLength(16);
            card.Property(c => c.NumberProtected).HasMaxLength(LoyaltyCard.MaxProtectedLength).IsRequired();
            card.HasOne<AppUser>().WithMany().HasForeignKey(c => c.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
            card.HasIndex(c => c.Store).IsUnique();
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
