using System.Security.Claims;
using Basket.Api.Data;
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
    string? Store);

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
        Store? Store);

    private static ListItemResponse ToResponse(Row r) =>
        new(r.Id, r.Text, r.Checked, r.AddedAt, r.AddedBy, r.CheckedAt, r.CheckedBy, r.ProductId, r.Store?.Slug());

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
            i.Store);

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
                if (!await db.StoreListings.AnyAsync(l => l.ProductId == productId && l.Store == parsed, ct))
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
            ProductId = request.ProductId,
            Store = shop,
        };
        db.ListItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (request.ProductId is not null && IsUniqueViolation(ex))
        {
            // Another phone added the same line a moment ago; that one is the answer.
            var winner = await OpenLineAsync(db, request.ProductId.Value, shop, ct);
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

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
        int rows;
        try
        {
            rows = await db.ListItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
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

        if (rows == 0)
        {
            return Results.NotFound();
        }

        return Results.Ok(ToResponse(await Project(db, id).SingleAsync(ct)));
    }

    private static async Task<IResult> DeleteItem(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.ListItems.Where(i => i.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
}
