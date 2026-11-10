using System.Security.Claims;
using Basket.Api.Auth;
using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices;

public sealed record AddProductRequest(string? Name);

public sealed record LinkStoreRequest(string? Url, string? ImageUrl = null);

public sealed record ListingResponse(string Url, string? Title, DateTimeOffset? CheckedAt, string? Error, string? Category, string? ImageUrl);

/// <summary>
/// One dated reading. <see cref="State"/>: <c>current</c> (usable today), <c>upcoming</c> (the offer starts later),
/// <c>ended</c> (the offer ran out since the reading) or <c>unavailable</c> (the shop lists no price).
/// <see cref="Best"/> is the lowest price a card holder pays today, when <c>current</c>.
/// </summary>
public sealed record PriceResponse(
    DateOnly ObservedOn,
    string State,
    decimal? Price,
    decimal? WasPrice,
    decimal? LoyaltyPrice,
    string? LoyaltyScheme,
    string? PromoText,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    decimal? Best,
    bool InStock);

/// <summary>
/// The last price really paid, from Lidl Plus receipts. <c>known</c> with the price and day; <c>none</c> when Lidl Plus is connected
/// but no receipt has this product; <c>not_connected</c> until the owner has pasted the Lidl Plus token.
/// </summary>
public sealed record PaidResponse(string State, decimal? Price, DateOnly? PaidOn);

public sealed record ProductStoreResponse(
    string Store,
    string Name,
    string ReadBy,
    ListingResponse? Listing,
    PriceResponse? Price,
    PaidResponse? LastPaid);

public sealed record ProductResponse(Guid Id, string Name, IReadOnlyList<ProductStoreResponse> Stores);

public sealed record RunResponse(DateTimeOffset FinishedAt, string Outcome, int Listings, int Read, string? Message);

public sealed record StoreStatusResponse(string Store, string Name, string ReadBy, RunResponse? LastRun);

public static class ProductEndpoints
{
    /// <summary>A guard against a runaway client; the nightly pull makes about one request per linked product.</summary>
    public const int MaxProducts = 300;

    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    public static void MapProductEndpoints(this IEndpointRouteBuilder api)
    {
        var products = api.MapGroup("/products");
        // The only three routes the fleet identity may call (scopes basket:catalog-read, and basket:catalog-add for the
        // add and the store-page link); the rest of the group, like every other endpoint, takes a signed-in person only.
        products.MapGet("/", GetProducts).RequireAuthorization(Policies.CatalogRead);
        products.MapPost("/", AddProduct).RequireAuthorization(Policies.CatalogAdd);
        products.MapDelete("/{id:guid}", DeleteProduct);
        products.MapPut("/{id:guid}/stores/{store}", LinkStore).RequireAuthorization(Policies.CatalogAdd);
        products.MapDelete("/{id:guid}/stores/{store}", UnlinkStore);
    }

    private static async Task<IResult> GetProducts(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var today = Today(clock);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var listings = (await db.StoreListings.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.ProductId);
        var latest = await LatestObservationsAsync(db, ct);
        var connected = await db.LidlPlusConnections.AnyAsync(ct);
        var paid = (await db.PaidPrices.AsNoTracking().ToListAsync(ct))
            .GroupBy(p => (p.ProductId, p.Store))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PaidOn).First());

        var response = products.Select(product => new ProductResponse(
            product.Id,
            product.Name,
            Stores.All.Select(store =>
            {
                var listing = listings[product.Id].FirstOrDefault(l => l.Store == store);
                return new ProductStoreResponse(
                    store.Slug(),
                    store.DisplayName(),
                    store.ReadBy(),
                    listing is null ? null : new ListingResponse(listing.Url, listing.Title, listing.LastCheckedAt, listing.LastError, listing.Category, listing.ImageUrl),
                    listing is not null && latest.TryGetValue(listing.Id, out var observation) ? ToPrice(observation, today) : null,
                    LastPaid(store, paid.GetValueOrDefault((product.Id, store)), connected));
            }).ToList())).ToList();

        var runs = await db.PricePullRuns.AsNoTracking()
            .GroupBy(r => r.Store)
            .Select(g => g.OrderByDescending(r => r.FinishedAt).First())
            .ToListAsync(ct);
        var stores = Stores.All.Select(store =>
        {
            var run = runs.FirstOrDefault(r => r.Store == store);
            return new StoreStatusResponse(
                store.Slug(),
                store.DisplayName(),
                store.ReadBy(),
                run is null ? null : new RunResponse(run.FinishedAt, run.Outcome.ToString().ToLowerInvariant(), run.Listings, run.Read, run.Message));
        }).ToList();

        return Results.Ok(new { products = response, stores });
    }

    private static async Task<IResult> AddProduct(
        AddProductRequest request,
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var name = UserNames.Clean(request.Name, Product.MaxNameLength);
        if (name is null)
        {
            return Results.Json(new { error = "name_required", message = "Type the product's name." }, statusCode: 400);
        }

        if (await db.Products.CountAsync(ct) >= MaxProducts)
        {
            return Results.Json(new { error = "products_full", message = "That is as many products as basket tracks. Delete some first." }, statusCode: 409);
        }

        if (await db.Products.AnyAsync(p => p.Name.ToLower() == name.ToLower(), ct))
        {
            return Results.Json(new { error = "product_exists", message = "That product is already tracked." }, statusCode: 409);
        }

        var addedBy = await AddedByAsync(http, db, ct);
        if (addedBy is null)
        {
            return Results.Json(new { error = "no_owner", message = "There is no owner to record the product against yet." }, statusCode: 409);
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            AddedAt = clock.GetUtcNow(),
            AddedByUserId = addedBy.Value,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        var connected = await db.LidlPlusConnections.AnyAsync(ct);
        var created = new ProductResponse(
            product.Id,
            product.Name,
            Stores.All.Select(s => new ProductStoreResponse(s.Slug(), s.DisplayName(), s.ReadBy(), null, null, LastPaid(s, null, connected))).ToList());
        return Results.Created($"/api/products/{product.Id}", created);
    }

    /// <summary>
    /// Who a new product is recorded against. A person adds it as themselves. The fleet identity is not a user and
    /// <c>AddedByUserId</c> is a required foreign key to one, so its products are recorded against the owner, who
    /// owns the fleet identity (no schema change); the log line names the fleet client that added it.
    /// </summary>
    private static async Task<Guid?> AddedByAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!FleetAgent.HasBearer(http.Request))
        {
            return Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }

        return await OwnerIdAsync(db, ct);
    }

    /// <summary>The household owner (the first one, should there ever be more), who anything made without a person is recorded against.</summary>
    internal static async Task<Guid?> OwnerIdAsync(AppDbContext db, CancellationToken ct) =>
        await (
            from u in db.Users.AsNoTracking()
            join ur in db.UserRoles on u.Id equals ur.UserId
            join r in db.Roles on ur.RoleId equals r.Id
            where r.Name == Roles.Owner
            orderby u.CreatedAt
            select (Guid?)u.Id).FirstOrDefaultAsync(ct);

    private static async Task<IResult> DeleteProduct(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();

    private static async Task<IResult> LinkStore(
        Guid id,
        string store,
        LinkStoreRequest request,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!Stores.TryParse(store, out var shop))
        {
            return Results.NotFound();
        }

        if (!await db.Products.AnyAsync(p => p.Id == id, ct))
        {
            return Results.NotFound();
        }

        if (!Stores.TryNormalizeProductUrl(shop, request.Url, out var url))
        {
            return Results.Json(
                new { error = "url_invalid", message = $"Paste the address of the product's {shop.DisplayName()} page, like {shop.UrlExample()}." },
                statusCode: 400);
        }

        string? imageUrl = null;
        if (!string.IsNullOrWhiteSpace(request.ImageUrl) && !ImageUrls.TryNormalize(request.ImageUrl, out imageUrl))
        {
            return Results.Json(new { error = "image_invalid", message = "The photo has to be an https address." }, statusCode: 400);
        }

        var listing = await db.StoreListings.SingleOrDefaultAsync(l => l.ProductId == id && l.Store == shop, ct);
        if (listing is null)
        {
            listing = new StoreListing { Id = Guid.NewGuid(), ProductId = id, Store = shop, AddedAt = clock.GetUtcNow() };
            db.StoreListings.Add(listing);
        }
        else if (listing.Url != url.ToString())
        {
            // A different page is a different product: forget what the old one said.
            listing.Title = null;
            listing.ImageUrl = null;
            listing.Category = null;
            listing.LastCheckedAt = null;
            listing.LastError = null;
            await db.PriceObservations.Where(o => o.ListingId == listing.Id).ExecuteDeleteAsync(ct);
        }

        listing.Url = url.ToString();
        listing.ImageUrl = imageUrl ?? listing.ImageUrl;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ListingResponse(listing.Url, listing.Title, listing.LastCheckedAt, listing.LastError, listing.Category, listing.ImageUrl));
    }

    private static async Task<IResult> UnlinkStore(Guid id, string store, AppDbContext db, CancellationToken ct) =>
        !Stores.TryParse(store, out var shop) || await db.StoreListings.Where(l => l.ProductId == id && l.Store == shop).ExecuteDeleteAsync(ct) == 0
            ? Results.NotFound()
            : Results.NoContent();

    private static async Task<Dictionary<Guid, PriceObservation>> LatestObservationsAsync(AppDbContext db, CancellationToken ct) =>
        (await db.PriceObservations.AsNoTracking()
            .GroupBy(o => o.ListingId)
            .Select(g => g.OrderByDescending(o => o.ObservedOn).First())
            .ToListAsync(ct))
        .ToDictionary(o => o.ListingId);

    private static PaidResponse? LastPaid(Store store, PaidPrice? paid, bool connected) =>
        store != Store.Lidl
            ? null
            : paid is not null
                ? new PaidResponse("known", paid.Price, paid.PaidOn)
                // Receipts come in through the owner's Lidl Plus connection; without it nothing can be known.
                : new PaidResponse(connected ? "none" : "not_connected", null, null);

    private static PriceResponse ToPrice(PriceObservation o, DateOnly today)
    {
        var state = PriceStates.Of(o, today);
        decimal? best = state == PriceStates.Current
            ? new[] { o.Price, o.LoyaltyPrice }.Where(p => p is not null).Min()
            : null;
        return new PriceResponse(o.ObservedOn, state, o.Price, o.WasPrice, o.LoyaltyPrice, o.LoyaltyScheme, o.PromoText, o.ValidFrom, o.ValidUntil, best, o.InStock);
    }

    private static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Dublin).DateTime);
}
