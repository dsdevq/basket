using System.Text.RegularExpressions;
using Basket.Api.Auth;
using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Catalog;

/// <summary>
/// One catalog product as a list shows it. <see cref="Price"/> is the latest reading, shaped as on the shelf
/// (<see cref="PriceResponse"/>), so <c>ObservedOn</c> is the Dublin day that value first appeared; <see cref="LastSeenAt"/> is the
/// last time a sweep listed the product at all. <see cref="UnitPrice"/> is the shop's own comparison price per
/// <see cref="UnitPriceBasis"/>, as the shop writes it.
/// </summary>
public sealed record CatalogItemResponse(
    Guid Id,
    string Store,
    string StoreName,
    string Name,
    string? Brand,
    string? SizeText,
    decimal? UnitPrice,
    string? UnitPriceBasis,
    string? ImageUrl,
    string Url,
    PriceResponse? Price,
    DateTimeOffset LastSeenAt);

public sealed record CatalogSearchResponse(string Query, string? Store, IReadOnlyList<CatalogItemResponse> Items);

/// <summary>
/// One catalog product in full: the list fields plus what only a detail view needs. A product the shops no longer list is still
/// answered, with <see cref="DiscontinuedAt"/> set.
/// </summary>
public sealed record CatalogProductResponse(
    Guid Id,
    string Store,
    string StoreName,
    string Name,
    string? Brand,
    string? SizeText,
    decimal? SizeAmount,
    string? SizeUnit,
    int SizePacks,
    decimal? UnitPrice,
    string? UnitPriceBasis,
    string? ImageUrl,
    string Url,
    string? Barcode,
    string? Category,
    PriceResponse? Price,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DiscontinuedAt);

/// <summary>
/// Reading the combined catalog: <c>GET /api/catalog/search</c> and <c>GET /api/catalog/{id}</c>. Both take the same policy as the
/// shelf's product list (a signed-in person, or the fleet identity holding <c>basket:catalog-read</c>); nothing here writes.
/// </summary>
public static partial class CatalogEndpoints
{
    /// <summary>Most products one search returns: a screen of tiles, and the cap on a runaway client.</summary>
    public const int MaxResults = 50;

    public const int MaxQueryLength = 80;

    public const int MinQueryLength = 2;

    private const string TsConfig = "english";

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder api)
    {
        var catalog = api.MapGroup("/catalog").RequireAuthorization(Policies.CatalogRead);
        catalog.MapGet("/search", SearchAsync);
        catalog.MapGet("/{id:guid}", GetAsync);
    }

    private static async Task<IResult> SearchAsync(string? q, string? store, int? limit, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var tokens = Tokens(q);
        if (tokens.Count == 0 || string.Join(' ', tokens).Length < MinQueryLength)
        {
            return Results.Json(new { error = "query_too_short", message = "Type at least two letters to search." }, statusCode: 400);
        }

        Store? shop = null;
        if (!string.IsNullOrWhiteSpace(store))
        {
            if (!Stores.TryParse(store, out var parsed))
            {
                return Results.Json(new { error = "unknown_store", message = "That is not a shop basket reads." }, statusCode: 400);
            }

            shop = parsed;
        }

        var take = Math.Clamp(limit ?? MaxResults, 1, MaxResults);

        // Every word is a prefix, all of them must match: "choc milk" finds "Milk Chocolate" and a half-typed word still finds
        // something. The words are letters and digits only, so nothing here can be query syntax.
        var tsQuery = string.Join(" & ", tokens.Select(t => t + ":*"));
        var matches = db.StoreProducts.AsNoTracking()
            .Where(p => p.DiscontinuedAt == null && p.SearchVector.Matches(EF.Functions.ToTsQuery(TsConfig, tsQuery)));
        if (shop is { } only)
        {
            matches = matches.Where(p => p.Store == only);
        }

        // Rank first, then name and id, so equal ranks (the usual case) come back in the same order every time.
        var found = await matches
            .OrderByDescending(p => p.SearchVector.Rank(EF.Functions.ToTsQuery(TsConfig, tsQuery)))
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Take(take)
            .ToListAsync(ct);

        var prices = await LatestPricesAsync(db, found.Select(p => p.Id).ToList(), ct);
        var today = PriceRecorder.DublinDay(clock.GetUtcNow());
        var items = found.Select(p => ToItem(p, prices.GetValueOrDefault(p.Id), today)).ToList();
        return Results.Ok(new CatalogSearchResponse(string.Join(' ', tokens), shop?.Slug(), items));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var product = await db.StoreProducts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var price = (await LatestPricesAsync(db, [id], ct)).GetValueOrDefault(id);
        var today = PriceRecorder.DublinDay(clock.GetUtcNow());
        return Results.Ok(new CatalogProductResponse(
            product.Id,
            product.Store.Slug(),
            product.Store.DisplayName(),
            product.Name,
            product.Brand,
            product.SizeText,
            product.SizeAmount,
            product.SizeUnit,
            product.SizePacks,
            product.UnitPrice,
            product.UnitPriceBasis,
            product.ImageUrl,
            product.Url,
            product.Barcode,
            product.Category,
            price is null ? null : ToPrice(price, today),
            product.FirstSeenAt,
            product.LastSeenAt,
            product.DiscontinuedAt));
    }

    /// <summary>The words of a search, lower-cased: runs of letters and digits, at most 8 of them within the length cap.</summary>
    private static List<string> Tokens(string? raw)
    {
        var text = raw is null ? string.Empty : raw.Length > MaxQueryLength ? raw[..MaxQueryLength] : raw;
        return Word().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(8).ToList();
    }

    private static async Task<Dictionary<Guid, StoreProductPrice>> LatestPricesAsync(AppDbContext db, List<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? []
            : (await db.StoreProductPrices.AsNoTracking()
                .Where(p => ids.Contains(p.StoreProductId))
                .GroupBy(p => p.StoreProductId)
                .Select(g => g.OrderByDescending(p => p.ObservedOn).First())
                .ToListAsync(ct))
            .ToDictionary(p => p.StoreProductId);

    private static CatalogItemResponse ToItem(StoreProduct p, StoreProductPrice? price, DateOnly today) => new(
        p.Id,
        p.Store.Slug(),
        p.Store.DisplayName(),
        p.Name,
        p.Brand,
        p.SizeText,
        p.UnitPrice,
        p.UnitPriceBasis,
        p.ImageUrl,
        p.Url,
        price is null ? null : ToPrice(price, today),
        p.LastSeenAt);

    private static PriceResponse ToPrice(StoreProductPrice p, DateOnly today)
    {
        var state = PriceStates.Of(p.Price, p.LoyaltyPrice, p.ValidFrom, p.ValidUntil, today);
        decimal? best = state == PriceStates.Current
            ? new[] { p.Price, p.LoyaltyPrice }.Where(x => x is not null).Min()
            : null;
        return new PriceResponse(p.ObservedOn, state, p.Price, p.WasPrice, p.LoyaltyPrice, p.LoyaltyScheme, p.PromoText, p.ValidFrom, p.ValidUntil, best, p.InStock);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
