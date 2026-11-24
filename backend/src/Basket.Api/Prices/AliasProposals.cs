using System.Security.Claims;
using Basket.Api.Data;
using Basket.Api.Lists;
using Basket.Api.Matching;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices;

/// <summary>
/// One proposed pair: <see cref="Text"/> (typed on the list, "Яйця") is probably <see cref="ProductName"/> (a receipt line, "Free Range
/// Eggs"). <see cref="Trips"/> is how many Lidl Plus trips had both a ticked-off line with those words and a receipt line of the product;
/// <see cref="LastSeen"/> is the Dublin day of the latest of them.
/// </summary>
public sealed record AliasProposalResponse(string Text, Guid ProductId, string ProductName, int Trips, DateOnly LastSeen);

public sealed record AliasProposalsResponse(IReadOnlyList<AliasProposalResponse> Proposals);

public sealed record AliasPairRequest(string? Text, Guid? ProductId);

/// <summary>
/// Pairs worth asking about, found without any guessing engine: <c>GET /api/alias-proposals</c> lists them, <c>POST .../accept</c> saves
/// one as a name of the product (the slice 1 alias, nothing else) and <c>POST .../reject</c> remembers a "no" so the pair never comes
/// back. Nothing is saved except by a tap. Like the product group these take a signed-in person only.
/// </summary>
public static class AliasProposals
{
    /// <summary>Most proposals asked for at once, and most products proposed for one set of words: a trip with many typed lines pairs each with each.</summary>
    public const int MaxProposals = 20;

    public const int MaxPerText = 3;

    public static void MapAliasProposalEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/alias-proposals");
        group.MapGet("/", List);
        group.MapPost("/accept", Accept);
        group.MapPost("/reject", Reject);
    }

    /// <summary>
    /// A Lidl Plus receipt that took over a tick-off trip (issue #184) holds two kinds of line: the ones the person ticked (the shop is set
    /// to Lidl, and a typed one has no product) and the receipt's own lines (no shop, a product). Every typed line paired with every
    /// receipt-only product of the same trip is a candidate; a pair seen on more trips ranks first, and within a count the trip with fewer
    /// receipt-only lines counts for more, since there the words had fewer products to be. Pairs already turned down, words that already are
    /// a name of some product, and a product's own name are left out.
    /// </summary>
    public static async Task<IReadOnlyList<AliasProposalResponse>> ComputeAsync(AppDbContext db, CancellationToken ct)
    {
        var receiptTrips = (await db.Trips.AsNoTracking()
                .Where(t => t.Source == TripSources.LidlPlus && t.ClosedAt != null)
                .Select(t => new { t.Id, ClosedAt = t.ClosedAt!.Value })
                .ToListAsync(ct))
            .ToDictionary(t => t.Id, t => PriceRecorder.DublinDay(t.ClosedAt));
        if (receiptTrips.Count == 0)
        {
            return [];
        }

        var lines = (await db.TripItems.AsNoTracking()
                .Where(i => db.Trips.Any(t => t.Id == i.TripId && t.Source == TripSources.LidlPlus))
                .Select(i => new { i.TripId, i.Text, i.ProductId, i.BoughtStore })
                .ToListAsync(ct))
            .ToLookup(i => i.TripId);

        var taken = (await db.ProductAliases.AsNoTracking().Select(a => a.NormalizedText).ToListAsync(ct)).ToHashSet();
        var rejected = (await db.AliasRejections.AsNoTracking().Select(r => new { r.NormalizedText, r.ProductId }).ToListAsync(ct))
            .Select(r => (r.NormalizedText, r.ProductId))
            .ToHashSet();
        var names = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var full = (await db.ProductAliases.AsNoTracking()
                .GroupBy(a => a.ProductId)
                .Select(g => new { ProductId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Where(g => g.Count >= ProductAlias.MaxPerProduct)
            .Select(g => g.ProductId)
            .ToHashSet();

        var pairs = new Dictionary<(string Key, Guid ProductId), Tally>();
        foreach (var (tripId, day) in receiptTrips)
        {
            var ticked = new Dictionary<string, string>();
            var bought = new HashSet<Guid>();
            foreach (var line in lines[tripId])
            {
                if (line.BoughtStore is not null && line.ProductId is null)
                {
                    var words = UserNames.Clean(line.Text, ProductAlias.MaxLength);
                    if (words is not null && !taken.Contains(ProductAliases.Normalize(words)))
                    {
                        ticked.TryAdd(ProductAliases.Normalize(words), words);
                    }
                }
                else if (line.BoughtStore is null && line.ProductId is { } product && names.ContainsKey(product))
                {
                    bought.Add(product);
                }
            }

            foreach (var (key, words) in ticked)
            {
                foreach (var product in bought)
                {
                    if (rejected.Contains((key, product)) || full.Contains(product) || key == ProductAliases.Normalize(names[product]))
                    {
                        continue;
                    }

                    var tally = pairs.TryGetValue((key, product), out var seen) ? seen : new Tally(words);
                    tally.Trips++;
                    tally.Weight += 1d / bought.Count;
                    if (day >= tally.LastSeen)
                    {
                        tally.LastSeen = day;
                        tally.Text = words;
                    }

                    pairs[(key, product)] = tally;
                }
            }
        }

        return pairs
            .OrderByDescending(p => p.Value.Trips)
            .ThenByDescending(p => p.Value.Weight)
            .ThenBy(p => names[p.Key.ProductId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Key.Key, StringComparer.Ordinal)
            .GroupBy(p => p.Key.Key)
            .SelectMany(g => g.Take(MaxPerText))
            .OrderByDescending(p => p.Value.Trips)
            .ThenByDescending(p => p.Value.Weight)
            .ThenBy(p => names[p.Key.ProductId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Key.Key, StringComparer.Ordinal)
            .Take(MaxProposals)
            .Select(p => new AliasProposalResponse(p.Value.Text, p.Key.ProductId, names[p.Key.ProductId], p.Value.Trips, p.Value.LastSeen))
            .ToList();
    }

    private sealed class Tally(string text)
    {
        public string Text { get; set; } = text;

        public int Trips { get; set; }

        public double Weight { get; set; }

        public DateOnly LastSeen { get; set; }
    }

    private static async Task<IResult> List(AppDbContext db, CancellationToken ct) =>
        Results.Ok(new AliasProposalsResponse(await ComputeAsync(db, ct)));

    private static async Task<IResult> Accept(
        AliasPairRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ServerCandidates candidates,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (UserNames.Clean(request.Text, ProductAlias.MaxLength) is not { } words || request.ProductId is not { } productId)
        {
            return Results.Json(new { error = "pair_required", message = "Say which words and which product." }, statusCode: 400);
        }

        var key = ProductAliases.Normalize(words);
        var proposal = (await ComputeAsync(db, ct)).FirstOrDefault(p => p.ProductId == productId && ProductAliases.Normalize(p.Text) == key);
        var product = proposal is null ? null : await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (proposal is null || product is null)
        {
            return Results.Json(new { error = "not_proposed", message = "That pair is no longer proposed." }, statusCode: 404);
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var outcome = await ProductAliases.TryAddAsync(db, product, proposal.Text, userId, clock.GetUtcNow(), ct);
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
            return Results.Json(new { error = "alias_exists", message = "Another name with those words was just saved." }, statusCode: 409);
        }

        await ProductAliases.LookAgainAsync(candidates, product.Id, outcome.Alias!.Text, ct);
        return Results.Created($"/api/products/{product.Id}/aliases/{outcome.Alias.Id}", new AliasResponse(outcome.Alias.Id, outcome.Alias.Text));
    }

    private static async Task<IResult> Reject(AliasPairRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (UserNames.Clean(request.Text, ProductAlias.MaxLength) is not { } words || request.ProductId is not { } productId)
        {
            return Results.Json(new { error = "pair_required", message = "Say which words and which product." }, statusCode: 400);
        }

        if (!await db.Products.AnyAsync(p => p.Id == productId, ct))
        {
            return Results.NotFound();
        }

        var key = ProductAliases.Normalize(words);
        if (!await db.AliasRejections.AnyAsync(r => r.NormalizedText == key && r.ProductId == productId, ct))
        {
            db.AliasRejections.Add(new AliasRejection { Id = Guid.NewGuid(), ProductId = productId, NormalizedText = key, RejectedAt = clock.GetUtcNow() });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ListEndpoints.IsUniqueViolation(ex))
            {
                // Turned down on another phone at the same moment: the same answer.
            }
        }

        return Results.NoContent();
    }
}
