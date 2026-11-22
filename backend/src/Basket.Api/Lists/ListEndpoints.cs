using System.Security.Claims;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Plan;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Basket.Api.Lists;

/// <summary>
/// Free text, or a product picked from the Shelf. With <see cref="ProductId"/> the line is named after the product (the text
/// is ignored) and <see cref="Store"/>, when given, is the shop it was picked from and must be one that lists the product.
/// </summary>
public sealed record AddItemRequest(string? Text, Guid? ProductId = null, string? Store = null);

public sealed record SetCheckedRequest(bool? Checked);

/// <summary>
/// Links a typed line to a Shelf product. <see cref="Remember"/>: keep the line's words as another name of the product, so the same
/// words link on their own next time; without it the line is linked once.
/// </summary>
public sealed record LinkProductRequest(Guid? ProductId, bool? Remember = null);

/// <summary>The shop a ticked item was bought at, when it was not the one the plan suggested.</summary>
public sealed record SetBoughtAtRequest(string? Store);

public sealed record Person(Guid Id, string Name);

public sealed record ListItemResponse(
    Guid Id,
    string Text,
    bool Checked,
    DateTimeOffset AddedAt,
    Person AddedBy,
    DateTimeOffset? CheckedAt,
    Person? CheckedBy,
    Guid? ProductId,
    string? Store,
    string? ImageUrl,
    string? BoughtStore);

public static class ListEndpoints
{
    /// <summary>A guard against a runaway client, far above what a household list reaches.</summary>
    public const int MaxItems = 500;

    public static void MapListEndpoints(this IEndpointRouteBuilder api)
    {
        var list = api.MapGroup("/list");
        list.MapGet("/", GetList);
        list.MapPost("/items", AddItem);
        list.MapPatch("/items/{id:guid}", SetChecked);
        list.MapPatch("/items/{id:guid}/bought-at", SetBoughtAt);
        list.MapPut("/items/{id:guid}/product", LinkProduct);
        list.MapDelete("/items/{id:guid}", DeleteItem);
    }

    /// <summary>The row as the database gives it; <see cref="ToResponse"/> turns the shop into the slug the API speaks.</summary>
    private sealed record Row(
        Guid Id,
        string Text,
        bool Checked,
        DateTimeOffset AddedAt,
        Person AddedBy,
        DateTimeOffset? CheckedAt,
        Person? CheckedBy,
        Guid? ProductId,
        Store? Store,
        string? ImageUrl,
        Store? BoughtStore);

    private static ListItemResponse ToResponse(Row r) =>
        new(r.Id, r.Text, r.Checked, r.AddedAt, r.AddedBy, r.CheckedAt, r.CheckedBy, r.ProductId, r.Store?.Slug(), r.ImageUrl, r.BoughtStore?.Slug());

    private static IQueryable<Row> Project(AppDbContext db, Guid? id = null) =>
        from i in db.ListItems.AsNoTracking().Where(i => id == null || i.Id == id)
        join a in db.Users on i.AddedByUserId equals a.Id
        join c in db.Users on i.CheckedByUserId equals c.Id into checkers
        from c in checkers.DefaultIfEmpty()
        select new Row(
            i.Id,
            i.Text,
            i.IsChecked,
            i.AddedAt,
            new Person(a.Id, a.DisplayName),
            i.CheckedAt,
            c == null ? null : new Person(c.Id, c.DisplayName),
            i.ProductId,
            i.Store,
            // The photo is read from the product's shop page, not copied onto the line: the picked shop's when there is one,
            // else the first shop alphabetically (Aldi, Dunnes, Lidl, Tesco) that has one. A free-text line has none.
            db.StoreListings
                .Where(l => l.ProductId == i.ProductId && l.ImageUrl != null && (i.Store == null || l.Store == i.Store))
                .OrderBy(l => l.Store)
                .Select(l => l.ImageUrl)
                .FirstOrDefault(),
            // Where a ticked line was bought, from its record in the open trip; a closed trip's items point at no line.
            db.TripItems.Where(t => t.ListItemId == i.Id).Select(t => t.BoughtStore).FirstOrDefault());

    private static async Task<IResult> GetList(AppDbContext db, CancellationToken ct)
    {
        var all = (await Project(db).ToListAsync(ct)).Select(ToResponse).ToList();
        // To buy first, oldest on top; ticked ones after, latest tick first.
        var items = all.Where(i => !i.Checked).OrderBy(i => i.AddedAt)
            .Concat(all.Where(i => i.Checked).OrderByDescending(i => i.CheckedAt));
        return Results.Ok(new { items });
    }

    private static async Task<IResult> AddItem(
        AddItemRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        string? text;
        Store? shop = null;
        var linkedTo = request.ProductId;
        if (request.ProductId is { } productId)
        {
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct);
            if (product is null)
            {
                return Results.Json(new { error = "product_unknown", message = "That product is not on the shelf." }, statusCode: 400);
            }

            if (request.Store is not null)
            {
                if (!Stores.TryParse(request.Store, out var parsed))
                {
                    return Results.Json(new { error = "store_unknown", message = "basket does not know that shop." }, statusCode: 400);
                }

                shop = parsed;
                // A shop sells it when its page is linked, or when a receipt shows the household bought it there.
                if (!await db.StoreListings.AnyAsync(l => l.ProductId == productId && l.Store == parsed, ct)
                    && !await db.PaidPrices.AnyAsync(p => p.ProductId == productId && p.Store == parsed, ct))
                {
                    return Results.Json(new { error = "not_sold_there", message = $"{parsed.DisplayName()} is not linked for that product." }, statusCode: 400);
                }
            }

            // Tapping a tile twice is one line, not two: the open line for this product and shop is the answer.
            var open = await OpenLineAsync(db, productId, shop, ct);
            if (open is not null)
            {
                return Results.Ok(open);
            }

            text = product.Name;
        }
        else if (request.Store is not null)
        {
            return Results.Json(new { error = "store_needs_product", message = "A shop goes with a product." }, statusCode: 400);
        }
        else
        {
            text = UserNames.Clean(request.Text, ListItem.MaxTextLength);
            // Words the household has given a product as another name link to it: "Яйця" goes on the list as Free Range Eggs' line.
            if (text is not null && await ProductAliases.FindAsync(db, text, ct) is { } alias)
            {
                linkedTo = alias.ProductId;
                var open = await OpenLineAsync(db, alias.ProductId, null, ct);
                if (open is not null)
                {
                    return Results.Ok(open);
                }
            }
        }

        if (text is null)
        {
            return Results.Json(new { error = "text_required", message = "Type what to buy." }, statusCode: 400);
        }

        if (await db.ListItems.CountAsync(ct) >= MaxItems)
        {
            return Results.Json(new { error = "list_full", message = "The list is full. Delete some items first." }, statusCode: 409);
        }

        var item = new ListItem
        {
            Id = Guid.NewGuid(),
            Text = text,
            AddedAt = clock.GetUtcNow(),
            AddedByUserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            ProductId = linkedTo,
            Store = shop,
        };
        db.ListItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (linkedTo is not null && IsUniqueViolation(ex))
        {
            // Another phone added the same line a moment ago; that one is the answer.
            var winner = await OpenLineAsync(db, linkedTo.Value, shop, ct);
            if (winner is not null)
            {
                return Results.Ok(winner);
            }

            throw;
        }

        var created = ToResponse(await Project(db, item.Id).SingleAsync(ct));
        return Results.Created($"/api/list/items/{item.Id}", created);
    }

    private static async Task<ListItemResponse?> OpenLineAsync(AppDbContext db, Guid productId, Store? shop, CancellationToken ct)
    {
        var id = await db.ListItems.AsNoTracking()
            .Where(i => !i.IsChecked && i.ProductId == productId && i.Store == shop)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);
        return id is { } found ? ToResponse(await Project(db, found).SingleAsync(ct)) : null;
    }

    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Ticks or un-ticks a line, and records it in the open trip in the same transaction: the item, the shop it was bought at
    /// (the one the line was picked from, else the one the plan suggests, until <see cref="SetBoughtAt"/> changes it) and the
    /// time. Un-ticking takes the record back out. A line ticked a second time keeps the shop already recorded.
    /// </summary>
    private static async Task<IResult> SetChecked(
        Guid id,
        SetCheckedRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.Checked is not { } isChecked)
        {
            return Results.Json(new { error = "checked_required", message = "Say whether the item is ticked." }, statusCode: 400);
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OpenTrips.LockAsync(db, ct);

        var line = await db.ListItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (line is null)
        {
            return Results.NotFound();
        }

        var recorded = isChecked && await db.TripItems.AnyAsync(t => t.ListItemId == id, ct);
        var boughtAt = isChecked && !recorded ? await PlanEndpoints.PlannedStoreAsync(db, clock, line, ct) : null;
        try
        {
            await db.ListItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsChecked, isChecked)
                .SetProperty(i => i.CheckedAt, isChecked ? now : (DateTimeOffset?)null)
                .SetProperty(i => i.CheckedByUserId, isChecked ? userId : (Guid?)null), ct);
        }
        catch (Exception ex) when (ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Un-ticking a product that was added again since: the list already has it open.
            return Results.Json(new { error = "already_on_list", message = "That is already on the list again." }, statusCode: 409);
        }

        if (!isChecked)
        {
            await db.TripItems.Where(t => t.ListItemId == id).ExecuteDeleteAsync(ct);
        }
        else if (recorded)
        {
            await db.TripItems.Where(t => t.ListItemId == id).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CheckedAt, now)
                .SetProperty(t => t.CheckedByUserId, userId), ct);
        }
        else
        {
            var trip = await OpenTrips.GetOrStartAsync(db, ct);
            db.TripItems.Add(new TripItem
            {
                Id = Guid.NewGuid(),
                TripId = trip.Id,
                Text = line.Text,
                CheckedAt = now,
                CheckedByUserId = userId,
                ProductId = line.ProductId,
                // A shop only means something with its product (the add endpoint refuses one without).
                Store = line.ProductId is null ? null : line.Store,
                BoughtStore = boughtAt,
                ListItemId = id,
            });
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(ToResponse(await Project(db, id).SingleAsync(ct)));
    }

    /// <summary>
    /// Links a typed line to a product from the Shelf. The line keeps its words; it gains the product, so the plan prices it, the
    /// Shelf marks the product as on the list, and a trip it is already ticked into records the product. With <c>remember</c> the words
    /// also become another name of the product. Refused when the line is already linked, when the product already has an open line
    /// (a second one would be a duplicate), or when the words already mean another product.
    /// </summary>
    private static async Task<IResult> LinkProduct(
        Guid id,
        LinkProductRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ServerCandidates candidates,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.ProductId is not { } productId || await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct) is not { } product)
        {
            return Results.Json(new { error = "product_unknown", message = "That product is not on the shelf." }, statusCode: 400);
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OpenTrips.LockAsync(db, ct);

        var line = await db.ListItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (line is null)
        {
            return Results.NotFound();
        }

        if (line.ProductId is not null)
        {
            return Results.Json(new { error = "already_linked", message = "That line is already linked to a product." }, statusCode: 409);
        }

        if (!line.IsChecked && await db.ListItems.AnyAsync(i => !i.IsChecked && i.ProductId == productId && i.Store == null, ct))
        {
            return Results.Json(new { error = "already_on_list", message = $"{product.Name} is already on the list." }, statusCode: 409);
        }

        string? remembered = null;
        if (request.Remember == true && UserNames.Clean(line.Text, ProductAlias.MaxLength) is { } words && ProductAliases.Normalize(words) != ProductAliases.Normalize(product.Name))
        {
            var known = await ProductAliases.FindAsync(db, words, ct);
            if (known is { } other && other.ProductId != productId)
            {
                var owner = await db.Products.AsNoTracking().Where(p => p.Id == other.ProductId).Select(p => p.Name).FirstAsync(ct);
                return Results.Json(new { error = "alias_exists", message = $"“{words}” already means {owner}." }, statusCode: 409);
            }

            if (known is null)
            {
                var outcome = await ProductAliases.TryAddAsync(db, product, words, userId, clock.GetUtcNow(), ct);
                if (outcome.Refusal is not null)
                {
                    return outcome.Refusal;
                }

                remembered = words;
            }
        }

        try
        {
            await db.ListItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ProductId, productId), ct);
            await db.TripItems.Where(t => t.ListItemId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ProductId, productId), ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another phone put the product on the list a moment ago, or someone saved the same words as another name.
            return Results.Json(new { error = "conflict", message = "That changed a moment ago. Try again." }, statusCode: 409);
        }

        await transaction.CommitAsync(ct);
        if (remembered is not null)
        {
            await ProductAliases.LookAgainAsync(candidates, productId, remembered, ct);
        }

        return Results.Ok(ToResponse(await Project(db, id).SingleAsync(ct)));
    }

    /// <summary>The person changes where a ticked item was bought, when it was not the shop the plan suggested.</summary>
    private static async Task<IResult> SetBoughtAt(Guid id, SetBoughtAtRequest request, AppDbContext db, CancellationToken ct)
    {
        if (!Stores.TryParse(request.Store, out var shop))
        {
            return Results.Json(new { error = "store_unknown", message = "basket does not know that shop." }, statusCode: 400);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OpenTrips.LockAsync(db, ct);
        var rows = await db.TripItems.Where(t => t.ListItemId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.BoughtStore, shop), ct);
        if (rows == 0)
        {
            return await db.ListItems.AnyAsync(i => i.Id == id, ct)
                ? Results.Json(new { error = "not_ticked", message = "Tick the item first." }, statusCode: 409)
                : Results.NotFound();
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(ToResponse(await Project(db, id).SingleAsync(ct)));
    }

    /// <summary>Deleting a ticked line takes its record out of the open trip too: it was never bought.</summary>
    private static async Task<IResult> DeleteItem(Guid id, AppDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OpenTrips.LockAsync(db, ct);
        await db.TripItems.Where(t => t.ListItemId == id).ExecuteDeleteAsync(ct);
        var removed = await db.ListItems.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }
}
