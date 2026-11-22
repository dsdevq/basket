using System.Security.Claims;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices.Catalog;
using Basket.Api.Prices.Promotions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices;

/// <summary>
/// <see cref="CatalogId"/>: add the product from that catalog row (<c>GET /api/catalog/search</c>): its name (unless <see cref="Name"/> says
/// otherwise), its barcode, and the row's page and photo linked as the product's page in that shop.
/// </summary>
public sealed record AddProductRequest(string? Name = null, string? Barcode = null, Guid? CatalogId = null);

public sealed record LinkStoreRequest(string? Url, string? ImageUrl = null);

/// <summary>
/// <see cref="Confirmed"/>: a person chose this page as the product's equivalent here (see <c>StoreListing.ConfirmedAt</c>).
/// <see cref="Discontinued"/>: the shop's sweeps no longer list the catalog product this page is.
/// </summary>
public sealed record ListingResponse(string Url, string? Title, DateTimeOffset? CheckedAt, string? Error, string? Category, string? ImageUrl, bool Confirmed, bool Discontinued = false);

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
    PaidResponse? LastPaid,
    int Candidates = 0);

public sealed record ProductResponse(Guid Id, string Name, IReadOnlyList<ProductStoreResponse> Stores, string? Barcode, IReadOnlyList<AliasResponse>? Aliases = null);

public sealed record RunResponse(DateTimeOffset FinishedAt, string Outcome, int Listings, int Read, string? Message);

public sealed record StoreStatusResponse(string Store, string Name, string ReadBy, RunResponse? LastRun, CatalogHealthResponse Catalog);

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
        products.MapGet("/{id:guid}/stores/{store}/history", GetHistory);
    }

    private static async Task<IResult> GetProducts(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var today = Today(clock);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var listings = (await db.StoreListings.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.ProductId);
        var delisted = (await db.StoreListings.AsNoTracking()
                .Where(l => db.StoreProducts.Any(p => p.Id == l.StoreProductId && p.DiscontinuedAt != null))
                .Select(l => l.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var latest = await LatestObservationsAsync(db, ct);
        var connected = await db.LidlPlusConnections.AnyAsync(ct);
        var paid = (await db.PaidPrices.AsNoTracking().ToListAsync(ct))
            .GroupBy(p => (p.ProductId, p.Store))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PaidOn).First());

        var proposals = (await db.MatchCandidates.AsNoTracking()
                .Where(c => c.Status == CandidateStatus.Proposed)
                .GroupBy(c => new { c.ProductId, c.Store })
                .Select(g => new { g.Key.ProductId, g.Key.Store, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(g => (g.ProductId, g.Store), g => g.Count);

        var aliases = (await db.ProductAliases.AsNoTracking().OrderBy(a => a.AddedAt).ThenBy(a => a.Id).ToListAsync(ct)).ToLookup(a => a.ProductId);

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
                    listing is null ? null : new ListingResponse(listing.Url, listing.Title, listing.LastCheckedAt, listing.LastError, listing.Category, listing.ImageUrl, listing.ConfirmedAt is not null, delisted.Contains(listing.Id)),
                    listing is not null && latest.TryGetValue(listing.Id, out var observation) ? ToPrice(observation, today) : null,
                    LastPaid(store, paid.GetValueOrDefault((product.Id, store)), connected),
                    proposals.GetValueOrDefault((product.Id, store)));
            }).ToList(),
            product.Barcode,
            aliases[product.Id].Select(a => new AliasResponse(a.Id, a.Text)).ToList())).ToList();

        var runs = await db.PricePullRuns.AsNoTracking()
            .GroupBy(r => r.Store)
            .Select(g => g.OrderByDescending(r => r.FinishedAt).First())
            .ToListAsync(ct);
        var health = await CatalogHealth.ForAllAsync(db, clock.GetUtcNow(), ct);
        var stores = Stores.All.Select(store =>
        {
            var run = runs.FirstOrDefault(r => r.Store == store);
            return new StoreStatusResponse(
                store.Slug(),
                store.DisplayName(),
                store.ReadBy(),
                run is null ? null : new RunResponse(run.FinishedAt, run.Outcome.ToString().ToLowerInvariant(), run.Listings, run.Read, run.Message),
                health[store]);
        }).ToList();

        return Results.Ok(new { products = response, stores });
    }

    /// <summary>The last 90 days of one shop's readings for a product (see <see cref="PriceHistory"/>); an empty list when the page has none yet.</summary>
    private static async Task<IResult> GetHistory(Guid id, string store, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!Stores.TryParse(store, out var shop))
        {
            return Results.NotFound();
        }

        var listing = await db.StoreListings.AsNoTracking().FirstOrDefaultAsync(l => l.ProductId == id && l.Store == shop, ct);
        if (listing is null)
        {
            return Results.NotFound();
        }

        var today = Today(clock);
        var start = PriceHistory.WindowStart(today);
        var observations = await db.PriceObservations.AsNoTracking()
            .Where(o => o.ListingId == listing.Id && o.ObservedOn >= start && o.ObservedOn <= today)
            .ToListAsync(ct);
        return Results.Ok(PriceHistory.Build(id, shop, observations, today));
    }

    private static async Task<IResult> AddProduct(
        AddProductRequest request,
        HttpContext http,
        AppDbContext db,
        ServerCandidates candidates,
        PriceRecorder prices,
        TimeProvider clock,
        CancellationToken ct)
    {
        StoreProduct? row = null;
        if (request.CatalogId is { } catalogId)
        {
            row = await db.StoreProducts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == catalogId, ct);
            if (row is null)
            {
                return Results.Json(new { error = "catalog_not_found", message = "That product is not in the catalog." }, statusCode: 404);
            }

            if (row.DiscontinuedAt is not null)
            {
                return Results.Json(new { error = "catalog_discontinued", message = "The shop no longer lists that product." }, statusCode: 409);
            }
        }

        var name = UserNames.Clean(request.Name ?? (row is null ? null : CatalogNames.ShelfName(row)), Product.MaxNameLength);
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

        Uri? pageUrl = null;
        if (row is not null)
        {
            if (!Stores.TryNormalizeProductUrl(row.Store, row.Url, out pageUrl))
            {
                return Results.Json(new { error = "catalog_page_invalid", message = "That product's page cannot be linked." }, statusCode: 409);
            }

            // The same shop page already on the shelf under another name is the same product: one tap must not make a second copy.
            var page = pageUrl.ToString();
            if (await db.StoreListings.AnyAsync(l => l.StoreProductId == row.Id || (l.Store == row.Store && l.Url == page), ct))
            {
                return Results.Json(new { error = "product_exists", message = "That product is already tracked." }, statusCode: 409);
            }
        }

        string? barcode = null;
        if (!string.IsNullOrWhiteSpace(request.Barcode))
        {
            if (!Gtin.TryNormalize(request.Barcode, out barcode))
            {
                return Results.Json(new { error = "barcode_invalid", message = "That is not a valid barcode (8, 12, 13 or 14 digits with a correct check digit)." }, statusCode: 400);
            }
        }
        else if (row?.Barcode is { } catalogBarcode)
        {
            // The shop's own barcode; one that does not check out is no barcode at all, never a refusal of the add.
            Gtin.TryNormalize(catalogBarcode, out barcode);
        }

        if (barcode is not null && await db.Products.AnyAsync(p => p.Barcode == barcode, ct))
        {
            return MatchEndpoints.BarcodeExists();
        }

        var addedBy = await AddedByAsync(http, db, ct);
        if (addedBy is null)
        {
            return Results.Json(new { error = "no_owner", message = "There is no owner to record the product against yet." }, statusCode: 409);
        }

        var now = clock.GetUtcNow();
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            AddedAt = now,
            AddedByUserId = addedBy.Value,
            Barcode = barcode,
        };
        db.Products.Add(product);
        StoreListing? listing = null;
        if (row is not null && pageUrl is not null)
        {
            listing = await ListingFromCatalogAsync(db, prices, product.Id, row, pageUrl, !FleetAgent.HasBearer(http.Request), now, ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (barcode is not null)
        {
            return MatchEndpoints.BarcodeExists();
        }

        // A new product may already have equivalents in the catalog; look now rather than at the next sweep. Never fails the add.
        await candidates.RunForProductAsync(product.Id, ct);

        var connected = await db.LidlPlusConnections.AnyAsync(ct);
        var created = new ProductResponse(
            product.Id,
            product.Name,
            Stores.All.Select(s => new ProductStoreResponse(
                s.Slug(),
                s.DisplayName(),
                s.ReadBy(),
                listing is not null && listing.Store == s ? new ListingResponse(listing.Url, listing.Title, listing.LastCheckedAt, listing.LastError, listing.Category, listing.ImageUrl, listing.ConfirmedAt is not null) : null,
                null,
                LastPaid(s, null, connected))).ToList(),
            product.Barcode,
            []);
        return Results.Created($"/api/products/{product.Id}", created);
    }

    /// <summary>
    /// The product's page in the catalog row's shop, taken from the row: the address (linked to the row), the shop's name for it, its
    /// photo and category, and the row's latest price as a first reading (dated by the sweep that last listed the product), so the
    /// product is priced at once instead of at the next sweep. A person tapping the row has chosen the page, so it is confirmed, as a
    /// pasted link is; the fleet's is not. Applies to the context; the caller saves.
    /// </summary>
    private static async Task<StoreListing> ListingFromCatalogAsync(AppDbContext db, PriceRecorder prices, Guid productId, StoreProduct row, Uri page, bool byPerson, DateTimeOffset now, CancellationToken ct)
    {
        var listing = await PointListingAsync(db, null, productId, row.Store, page.ToString(), now, ct);
        // The row the person tapped, whatever the address's own id says.
        listing.StoreProductId = row.Id;
        listing.Title = PriceRecorder.Truncate(row.Name, 300);
        listing.ImageUrl = ImageUrls.TryNormalize(row.ImageUrl, out var image) ? image : null;
        listing.Category = PriceRecorder.Truncate(row.Category, StoreListing.MaxCategoryLength);
        listing.ConfirmedAt = byPerson ? now : null;

        var price = await db.StoreProductPrices.AsNoTracking()
            .Where(p => p.StoreProductId == row.Id)
            .OrderByDescending(p => p.ObservedOn)
            .FirstOrDefaultAsync(ct);
        // A reading with no price that is in stock says nothing a page can use (CatalogRecorder mirrors by the same rule).
        if (price is not null && (price.Price is not null || price.LoyaltyPrice is not null || !price.InStock))
        {
            var quote = new PriceQuote(
                row.Name, price.Price, price.WasPrice, price.LoyaltyPrice, price.LoyaltyScheme, price.PromoText, price.ValidFrom, price.ValidUntil, price.InStock, row.ImageUrl, row.Category);
            await prices.RecordAsync(listing, quote, row.LastSeenAt, ct);
        }

        return listing;
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
        HttpContext http,
        AppDbContext db,
        ServerCandidates candidates,
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

        var byPerson = !FleetAgent.HasBearer(http.Request);
        string? imageUrl = null;
        if (!string.IsNullOrWhiteSpace(request.ImageUrl) && !ImageUrls.TryNormalize(request.ImageUrl, out imageUrl))
        {
            return Results.Json(new { error = "image_invalid", message = "The photo has to be an https address." }, statusCode: 400);
        }

        var listing = await db.StoreListings.SingleOrDefaultAsync(l => l.ProductId == id && l.Store == shop, ct);
        if (!byPerson && listing?.ConfirmedAt is not null && listing.Url != url.ToString())
        {
            // A person chose that page; a machine does not get to swap it. Only a person relinking can.
            return Results.Json(
                new { error = "match_confirmed", message = $"{shop.DisplayName()} already has a confirmed page for this product." },
                statusCode: 409);
        }

        listing = await PointListingAsync(db, listing, id, shop, url.ToString(), clock.GetUtcNow(), ct);

        listing.ImageUrl = imageUrl ?? listing.ImageUrl;
        if (byPerson)
        {
            // A person pasting a page is choosing it: that is a confirmed match, and the shop's open proposals are settled.
            listing.ConfirmedAt = clock.GetUtcNow();
            await db.MatchCandidates.Where(c => c.ProductId == id && c.Store == shop && c.Status == CandidateStatus.Proposed).ExecuteDeleteAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        if (byPerson)
        {
            // A confirmed page tells the product its barcode, size and category: look again at the shops it still has no page in.
            await candidates.RunForProductAsync(id, ct);
        }

        return Results.Ok(new ListingResponse(listing.Url, listing.Title, listing.LastCheckedAt, listing.LastError, listing.Category, listing.ImageUrl, listing.ConfirmedAt is not null));
    }

    /// <summary>
    /// Makes the product's listing in a shop point at <paramref name="url"/>, adding it when there is none. A different page
    /// is a different product, so what the old one said (title, photo, category, readings) is forgotten. The listing is linked to
    /// the catalog row its address names, when the catalog has it (<see cref="CatalogLink"/>).
    /// </summary>
    internal static async Task<StoreListing> PointListingAsync(AppDbContext db, StoreListing? listing, Guid productId, Store shop, string url, DateTimeOffset now, CancellationToken ct)
    {
        if (listing is null)
        {
            listing = new StoreListing { Id = Guid.NewGuid(), ProductId = productId, Store = shop, AddedAt = now, Url = url };
            db.StoreListings.Add(listing);
            await CatalogLink.LinkAsync(db, listing, ct);
            return listing;
        }

        if (listing.Url != url)
        {
            listing.Title = null;
            listing.ImageUrl = null;
            listing.Category = null;
            listing.LastCheckedAt = null;
            listing.LastError = null;
            await db.PriceObservations.Where(o => o.ListingId == listing.Id).ExecuteDeleteAsync(ct);
            listing.Url = url;
        }

        // Linked on every pointing, not only a change: a page pointed at again is picked up by a catalog row that has arrived since.
        await CatalogLink.LinkAsync(db, listing, ct);
        return listing;
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
