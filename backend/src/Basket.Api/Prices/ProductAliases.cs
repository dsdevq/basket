using System.Security.Claims;
using Basket.Api.Data;
using Basket.Api.Lists;
using Basket.Api.Matching;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices;

public sealed record AliasRequest(string? Text);

public sealed record AliasResponse(Guid Id, string Text);

/// <summary>
/// A product's other names: <c>POST</c>, <c>PATCH</c> and <c>DELETE /api/products/{id}/aliases</c>. The list of them comes with the
/// product (<c>GET /api/products</c>). Like the rest of the product group these take a signed-in person only, never the fleet identity.
/// </summary>
public static class ProductAliases
{
    public static void MapProductAliasEndpoints(this IEndpointRouteBuilder api)
    {
        var aliases = api.MapGroup("/products/{id:guid}/aliases");
        aliases.MapPost("/", Add);
        aliases.MapPatch("/{aliasId:guid}", Rename);
        aliases.MapDelete("/{aliasId:guid}", Delete);
    }

    /// <summary>The words folded for comparison: case-insensitive, whitespace collapsed by <see cref="UserNames.Clean"/> first.</summary>
    public static string Normalize(string cleaned) => cleaned.ToLowerInvariant();

    /// <summary>The alias whose words are exactly <paramref name="text"/> (any letter case), or null. Used to link a typed line.</summary>
    public static async Task<ProductAlias?> FindAsync(AppDbContext db, string? text, CancellationToken ct)
    {
        var cleaned = UserNames.Clean(text, ListItem.MaxTextLength);
        if (cleaned is null)
        {
            return null;
        }

        var key = Normalize(cleaned);
        return await db.ProductAliases.AsNoTracking().SingleOrDefaultAsync(a => a.NormalizedText == key, ct);
    }

    /// <summary>Another name for every product that has one, for the matcher and the shelf: product id to its aliases' texts.</summary>
    public static async Task<ILookup<Guid, string>> TextsAsync(AppDbContext db, IReadOnlyCollection<Guid>? productIds, CancellationToken ct)
    {
        var query = db.ProductAliases.AsNoTracking();
        if (productIds is not null)
        {
            query = query.Where(a => productIds.Contains(a.ProductId));
        }

        return (await query.OrderBy(a => a.AddedAt).ThenBy(a => a.Id).Select(a => new { a.ProductId, a.Text }).ToListAsync(ct))
            .ToLookup(a => a.ProductId, a => a.Text);
    }

    /// <summary>The facts the matcher compares a catalog row against: the product's own name, then one set of facts per alias that has English words.</summary>
    public static IReadOnlyList<ProductFacts> FactsWithAliases(ProductFacts facts, IEnumerable<string> aliases)
    {
        var size = facts.Size ?? ProductSize.Parse(facts.Name);
        var all = new List<ProductFacts> { facts };
        // A name with no English words (Cyrillic, say) has nothing to compare: the matcher reads letters a-z and digits only.
        all.AddRange(aliases.Where(a => CandidateMatcher.KeyWord(a) is not null).Select(a => facts with { Name = a, Size = size }));
        return all;
    }

    public sealed record Outcome(ProductAlias? Alias, IResult? Refusal);

    /// <summary>
    /// Checks <paramref name="text"/> as another name for the product and, when it passes, adds the row to the context (the caller saves).
    /// Refused: no usable text, the product's own name, a name already taken (by this product or another), too many names.
    /// </summary>
    public static async Task<Outcome> TryAddAsync(AppDbContext db, Product product, string? text, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var (cleaned, refusal) = await CheckAsync(db, product, text, null, ct);
        if (refusal is not null)
        {
            return new Outcome(null, refusal);
        }

        if (await db.ProductAliases.CountAsync(a => a.ProductId == product.Id, ct) >= ProductAlias.MaxPerProduct)
        {
            return new Outcome(null, Results.Json(new { error = "aliases_full", message = $"{product.Name} has as many other names as basket keeps. Delete one first." }, statusCode: 409));
        }

        var alias = new ProductAlias
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Text = cleaned!,
            NormalizedText = Normalize(cleaned!),
            AddedAt = now,
            AddedByUserId = userId,
        };
        db.ProductAliases.Add(alias);
        return new Outcome(alias, null);
    }

    private static async Task<(string? Cleaned, IResult? Refusal)> CheckAsync(AppDbContext db, Product product, string? text, Guid? self, CancellationToken ct)
    {
        var cleaned = UserNames.Clean(text, ProductAlias.MaxLength);
        if (cleaned is null)
        {
            return (null, Results.Json(new { error = "alias_required", message = "Type the other name." }, statusCode: 400));
        }

        var key = Normalize(cleaned);
        if (key == Normalize(product.Name))
        {
            return (null, Results.Json(new { error = "alias_is_name", message = $"That is already the name of {product.Name}." }, statusCode: 409));
        }

        var taken = await db.ProductAliases.AsNoTracking()
            .Where(a => a.NormalizedText == key && a.Id != self)
            .Select(a => new { a.ProductId })
            .FirstOrDefaultAsync(ct);
        if (taken is not null)
        {
            var owner = taken.ProductId == product.Id
                ? product.Name
                : await db.Products.AsNoTracking().Where(p => p.Id == taken.ProductId).Select(p => p.Name).FirstAsync(ct);
            return (null, Results.Json(new { error = "alias_exists", message = $"“{cleaned}” already means {owner}." }, statusCode: 409));
        }

        return (cleaned, null);
    }

    private static IResult Taken() =>
        Results.Json(new { error = "alias_exists", message = "Another name with those words was just saved." }, statusCode: 409);

    private static async Task<IResult> Add(
        Guid id,
        AliasRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ServerCandidates candidates,
        TimeProvider clock,
        CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var outcome = await TryAddAsync(db, product, request.Text, userId, clock.GetUtcNow(), ct);
        if (outcome.Refusal is not null)
        {
            return outcome.Refusal;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ListEndpoints.IsUniqueViolation(ex))
        {
            return Taken();
        }

        await LookAgainAsync(candidates, product.Id, outcome.Alias!.Text, ct);
        return Results.Created($"/api/products/{id}/aliases/{outcome.Alias.Id}", new AliasResponse(outcome.Alias.Id, outcome.Alias.Text));
    }

    private static async Task<IResult> Rename(
        Guid id,
        Guid aliasId,
        AliasRequest request,
        AppDbContext db,
        ServerCandidates candidates,
        CancellationToken ct)
    {
        var alias = await db.ProductAliases.SingleOrDefaultAsync(a => a.Id == aliasId && a.ProductId == id, ct);
        var product = alias is null ? null : await db.Products.AsNoTracking().SingleAsync(p => p.Id == id, ct);
        if (alias is null || product is null)
        {
            return Results.NotFound();
        }

        var (cleaned, refusal) = await CheckAsync(db, product, request.Text, alias.Id, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        alias.Text = cleaned!;
        alias.NormalizedText = Normalize(cleaned!);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ListEndpoints.IsUniqueViolation(ex))
        {
            return Taken();
        }

        await LookAgainAsync(candidates, product.Id, alias.Text, ct);
        return Results.Ok(new AliasResponse(alias.Id, alias.Text));
    }

    private static async Task<IResult> Delete(Guid id, Guid aliasId, AppDbContext db, CancellationToken ct) =>
        await db.ProductAliases.Where(a => a.Id == aliasId && a.ProductId == id).ExecuteDeleteAsync(ct) == 0
            ? Results.NotFound()
            : Results.NoContent();

    /// <summary>A name with English words is one more thing the matcher can compare: look again at the shops the product has no page in. Never fails the save.</summary>
    internal static async Task LookAgainAsync(ServerCandidates candidates, Guid productId, string alias, CancellationToken ct)
    {
        if (CandidateMatcher.KeyWord(alias) is not null)
        {
            await candidates.RunForProductAsync(productId, ct);
        }
    }
}
