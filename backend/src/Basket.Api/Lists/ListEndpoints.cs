using System.Security.Claims;
using Basket.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Lists;

public sealed record AddItemRequest(string? Text);

public sealed record SetCheckedRequest(bool? Checked);

public sealed record Person(Guid Id, string Name);

public sealed record ListItemResponse(
    Guid Id,
    string Text,
    bool Checked,
    DateTimeOffset AddedAt,
    Person AddedBy,
    DateTimeOffset? CheckedAt,
    Person? CheckedBy);

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

    private static IQueryable<ListItemResponse> Project(AppDbContext db, Guid? id = null) =>
        from i in db.ListItems.AsNoTracking().Where(i => id == null || i.Id == id)
        join a in db.Users on i.AddedByUserId equals a.Id
        join c in db.Users on i.CheckedByUserId equals c.Id into checkers
        from c in checkers.DefaultIfEmpty()
        select new ListItemResponse(
            i.Id,
            i.Text,
            i.IsChecked,
            i.AddedAt,
            new Person(a.Id, a.DisplayName),
            i.CheckedAt,
            c == null ? null : new Person(c.Id, c.DisplayName));

    private static async Task<IResult> GetList(AppDbContext db, CancellationToken ct)
    {
        var all = await Project(db).ToListAsync(ct);
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
        var text = UserNames.Clean(request.Text, ListItem.MaxTextLength);
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
        };
        db.ListItems.Add(item);
        await db.SaveChangesAsync(ct);

        var created = await Project(db, item.Id).SingleAsync(ct);
        return Results.Created($"/api/list/items/{item.Id}", created);
    }

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
        var rows = await db.ListItems.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.IsChecked, isChecked)
            .SetProperty(i => i.CheckedAt, isChecked ? now : (DateTimeOffset?)null)
            .SetProperty(i => i.CheckedByUserId, isChecked ? userId : (Guid?)null), ct);
        if (rows == 0)
        {
            return Results.NotFound();
        }

        return Results.Ok(await Project(db, id).SingleAsync(ct));
    }

    private static async Task<IResult> DeleteItem(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.ListItems.Where(i => i.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
}
