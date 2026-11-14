using System.Text;
using Basket.Api.Prices;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Basket.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<ListItem> ListItems => Set<ListItem>();

    public DbSet<Trip> Trips => Set<Trip>();

    public DbSet<TripItem> TripItems => Set<TripItem>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<IngestToken> IngestTokens => Set<IngestToken>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<StoreListing> StoreListings => Set<StoreListing>();

    public DbSet<StoreProduct> StoreProducts => Set<StoreProduct>();

    public DbSet<StoreProductPrice> StoreProductPrices => Set<StoreProductPrice>();

    public DbSet<MatchCandidate> MatchCandidates => Set<MatchCandidate>();

    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();

    public DbSet<PricePullRun> PricePullRuns => Set<PricePullRun>();

    public DbSet<PaidPrice> PaidPrices => Set<PaidPrice>();

    public DbSet<LoyaltyCard> LoyaltyCards => Set<LoyaltyCard>();

    public DbSet<LidlPlusConnection> LidlPlusConnections => Set<LidlPlusConnection>();

    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

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
            item.Property(i => i.Store).HasConversion<string>().HasMaxLength(16);
            item.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.SetNull);
            // One open line per product and shop (a null shop counts as one value): tapping a Shelf tile twice, or two
            // phones tapping at once, cannot put the same thing on the list twice. A ticked line is history, not a duplicate.
            item.HasIndex(i => new { i.ProductId, i.Store })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("is_checked = false AND product_id IS NOT NULL");
        });

        builder.Entity<Trip>(trip =>
        {
            trip.ToTable("trip");
            trip.HasKey(t => t.Id);
            trip.HasIndex(t => t.ClosedAt);
            trip.Property(t => t.Source).HasMaxLength(16);
            trip.Property(t => t.ExternalId).HasMaxLength(64);
            trip.Property(t => t.Total).HasPrecision(10, 2);
            // One trip per receipt: importing the same receipt twice (a manual pull racing the nightly one) cannot file it twice.
            trip.HasIndex(t => new { t.Source, t.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        });

        builder.Entity<TripItem>(item =>
        {
            item.ToTable("trip_item");
            item.HasKey(i => i.Id);
            item.Property(i => i.Text).HasMaxLength(ListItem.MaxTextLength).IsRequired();
            item.HasOne<Trip>().WithMany().HasForeignKey(i => i.TripId).OnDelete(DeleteBehavior.Cascade);
            item.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CheckedByUserId).OnDelete(DeleteBehavior.SetNull);
            item.HasIndex(i => i.TripId);
            item.Property(i => i.Store).HasConversion<string>().HasMaxLength(16);
            item.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.SetNull);
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
            product.Property(p => p.Barcode).HasMaxLength(14);
            product.HasOne<AppUser>().WithMany().HasForeignKey(p => p.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
            product.HasIndex(p => p.Barcode).IsUnique();
        });

        builder.Entity<StoreListing>(listing =>
        {
            listing.ToTable("store_listing");
            listing.HasKey(l => l.Id);
            listing.Property(l => l.Store).HasConversion<string>().HasMaxLength(16);
            listing.Property(l => l.Url).HasMaxLength(400).IsRequired();
            listing.Property(l => l.Title).HasMaxLength(300);
            listing.Property(l => l.ImageUrl).HasMaxLength(ImageUrls.MaxLength);
            listing.Property(l => l.Category).HasMaxLength(StoreListing.MaxCategoryLength);
            listing.Property(l => l.LastError).HasMaxLength(500);
            listing.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            listing.HasIndex(l => new { l.ProductId, l.Store }).IsUnique();
            // The catalog row this page is, when linked. A catalog row is never deleted by a sweep (it is marked
            // discontinued), so the link only clears if a row is removed by hand.
            listing.HasOne<StoreProduct>().WithMany().HasForeignKey(l => l.StoreProductId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<StoreProduct>(catalog =>
        {
            catalog.ToTable("store_product");
            catalog.HasKey(p => p.Id);
            catalog.Property(p => p.Store).HasConversion<string>().HasMaxLength(16);
            catalog.Property(p => p.ExternalId).HasMaxLength(StoreProduct.MaxExternalIdLength).IsRequired();
            catalog.Property(p => p.Url).HasMaxLength(400).IsRequired();
            catalog.Property(p => p.Name).HasMaxLength(StoreProduct.MaxNameLength).IsRequired();
            catalog.Property(p => p.Brand).HasMaxLength(StoreProduct.MaxBrandLength);
            catalog.Property(p => p.SizeText).HasMaxLength(StoreProduct.MaxSizeTextLength);
            catalog.Property(p => p.SizeAmount).HasPrecision(12, 2);
            catalog.Property(p => p.SizeUnit).HasMaxLength(4);
            catalog.Property(p => p.SizePacks).HasDefaultValue(1);
            catalog.Property(p => p.UnitPrice).HasPrecision(10, 2);
            catalog.Property(p => p.UnitPriceBasis).HasMaxLength(StoreProduct.MaxUnitPriceBasisLength);
            catalog.Property(p => p.Barcode).HasMaxLength(14);
            catalog.Property(p => p.Category).HasMaxLength(StoreListing.MaxCategoryLength);
            catalog.Property(p => p.ImageUrl).HasMaxLength(ImageUrls.MaxLength);
            catalog.HasIndex(p => new { p.Store, p.ExternalId }).IsUnique();
            catalog.HasIndex(p => p.Barcode).HasFilter("barcode IS NOT NULL");
            catalog.HasIndex(p => new { p.Store, p.LastSeenAt });
            catalog.HasGeneratedTsVectorColumn(p => p.SearchVector, "english", p => new { p.Name, p.Brand })
                .HasIndex(p => p.SearchVector)
                .HasMethod("GIN");
        });

        builder.Entity<StoreProductPrice>(price =>
        {
            price.ToTable("store_product_price");
            price.HasKey(p => new { p.StoreProductId, p.ObservedOn });
            price.Property(p => p.Price).HasPrecision(10, 2);
            price.Property(p => p.WasPrice).HasPrecision(10, 2);
            price.Property(p => p.LoyaltyPrice).HasPrecision(10, 2);
            price.Property(p => p.LoyaltyScheme).HasMaxLength(32);
            price.Property(p => p.PromoText).HasMaxLength(200);
            price.HasOne<StoreProduct>().WithMany().HasForeignKey(p => p.StoreProductId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MatchCandidate>(candidate =>
        {
            candidate.ToTable("match_candidate");
            candidate.HasKey(c => c.Id);
            candidate.Property(c => c.Store).HasConversion<string>().HasMaxLength(16);
            candidate.Property(c => c.Url).HasMaxLength(400).IsRequired();
            candidate.Property(c => c.Title).HasMaxLength(300);
            candidate.Property(c => c.Barcode).HasMaxLength(14);
            candidate.Property(c => c.Basis).HasMaxLength(16).IsRequired();
            candidate.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            candidate.HasOne<Product>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Cascade);
            candidate.HasIndex(c => new { c.ProductId, c.Store, c.Url }).IsUnique();
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
            paid.HasIndex(p => new { p.ProductId, p.Store, p.PaidOn }).IsUnique();
        });

        builder.Entity<LoyaltyCard>(card =>
        {
            card.ToTable("loyalty_card");
            card.HasKey(c => c.Id);
            card.Property(c => c.Store).HasConversion<string>().HasMaxLength(16);
            card.Property(c => c.Format).HasConversion<string>().HasMaxLength(16);
            card.Property(c => c.NumberProtected).HasMaxLength(LoyaltyCard.MaxProtectedLength).IsRequired();
            card.HasIndex(c => c.Store).IsUnique();
        });

        builder.Entity<LidlPlusConnection>(connection =>
        {
            connection.ToTable("lidl_plus_connection");
            connection.HasKey(c => c.Id);
            connection.Property(c => c.Id).ValueGeneratedNever();
            connection.Property(c => c.RefreshTokenProtected).HasMaxLength(LidlPlusConnection.MaxProtectedLength).IsRequired();
            connection.Property(c => c.Problem).HasMaxLength(32);
        });

        builder.Entity<UserPreference>(preferences =>
        {
            preferences.ToTable("user_preferences");
            preferences.HasKey(p => p.UserId);
            preferences.Property(p => p.UserId).ValueGeneratedNever();
            preferences.Property(p => p.Theme).HasMaxLength(UserPreference.MaxThemeLength).IsRequired();
            preferences.Property(p => p.ThemeSeed).HasMaxLength(UserPreference.SeedLength);
            preferences.HasOne<AppUser>().WithOne().HasForeignKey<UserPreference>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
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
