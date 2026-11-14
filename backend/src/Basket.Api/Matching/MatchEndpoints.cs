using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Matching;

/// <summary>One result of a shop's search, as the reader or a person's client saw it.</summary>
public sealed record SearchResult(string? Url, string? Title, string? Barcode);

public sealed record ProposeRequest(string? Store, IReadOnlyList<SearchResult>? Results);

public sealed record SetBarcodeRequest(string? Barcode);

/// <summary>What happened to one posted search result: <c>proposed</c>, or the reason it was not.</summary>
public sealed record ProposeOutcome(string? Url, string Outcome);

public sealed record CandidateResponse(Guid Id, string Store, string Url, string? Title, string? Barcode, string Basis, int Score);

/// <summary>
/// Deterministic candidate matching. A shop's search results are posted against a product; each is checked by
/// <see cref="CandidateMatcher"/> and the ones that pass are stored as proposals, apart from the confirmed listings. The
/// app never fetches the other shops' search itself (tesco.ie, aldi.ie and the Dunnes grocery site refuse the VPS): the
/// PC reader, or a person, brings the results.
/// </summary>
public static class MatchEndpoints
{
    /// <summary>Most results one post may carry.</summary>
    public const int MaxResults = 50;

    /// <summary>Most open proposals kept per product and shop; a better one pushes the weakest out.</summary>
    public const int MaxProposalsPerStore = 5;

    public static void MapMatchEndpoints(this IEndpointRouteBuilder api)
    {
        var products = api.MapGroup("/products");
        // Same two scopes as the rest of the catalog: reading proposals is catalog-read, writing them or a barcode is
        // catalog-add. Confirming a proposal is a person's act and has no route here.
        products.MapGet("/{id:guid}/candidates", GetCandidates).RequireAuthorization(Policies.CatalogRead);
        products.MapPost("/{id:guid}/candidates", Propose).RequireAuthorization(Policies.CatalogAdd);
        products.MapPut("/{id:guid}/barcode", SetBarcode).RequireAuthorization(Policies.CatalogAdd);
        // The match sheet's two answers. No policy of their own: like the rest of the group they take a signed-in person
        // only, so a fleet token can read and propose but never decide.
        products.MapPost("/{id:guid}/candidates/{candidateId:guid}/confirm", Confirm);
        products.MapPost("/{id:guid}/candidates/{candidateId:guid}/reject", Reject);
    }

    private static async Task<IResult> GetCandidates(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == id, ct))
        {
            return Results.NotFound();
        }

        var open = await db.MatchCandidates.AsNoTracking()
            .Where(c => c.ProductId == id && c.Status == CandidateStatus.Proposed)
            .ToListAsync(ct);
        var ordered = open
            .OrderBy(c => c.Store)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Url, StringComparer.Ordinal)
            .Select(c => new CandidateResponse(c.Id, c.Store.ToString().ToLowerInvariant(), c.Url, c.Title, c.Barcode, c.Basis, c.Score))
            .ToList();
        return Results.Ok(new { candidates = ordered });
    }

    private static async Task<IResult> Propose(Guid id, ProposeRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!Stores.TryParse(request.Store, out var store))
        {
            return Results.Json(new { error = "unknown_store", message = "Name one of lidl, aldi, tesco or dunnes." }, statusCode: 400);
        }

        if (request.Results is not { Count: > 0 and <= MaxResults } results)
        {
            return Results.Json(new { error = "results_invalid", message = $"Send between 1 and {MaxResults} search results." }, statusCode: 400);
        }

        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var listing = await db.StoreListings.AsNoTracking().SingleOrDefaultAsync(l => l.ProductId == id && l.Store == store, ct);
        if (listing?.ConfirmedAt is not null)
        {
            // The owner already chose this shop's page. Matching never proposes beside it, let alone over it.
            return Results.Json(
                new { error = "match_confirmed", message = $"{store.DisplayName()} already has a confirmed page for this product." },
                statusCode: 409);
        }

        var facts = new ProductFacts(product.Name, product.Barcode);
        var all = await db.MatchCandidates.Where(c => c.ProductId == id && c.Store == store).ToDictionaryAsync(c => c.Url, StringComparer.Ordinal, ct);
        var existing = all.Where(c => c.Value.Status == CandidateStatus.Proposed).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var outcomes = new List<ProposeOutcome>(results.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passed = new List<(MatchCandidate Row, bool IsNew, int OutcomeIndex)>();

        foreach (var result in results)
        {
            if (result is null || !Stores.TryNormalizeProductUrl(store, result.Url, out var uri))
            {
                outcomes.Add(new ProposeOutcome(result?.Url, "url_invalid"));
                continue;
            }

            var url = uri.ToString();
            if (!seen.Add(url))
            {
                outcomes.Add(new ProposeOutcome(url, "duplicate"));
                continue;
            }

            if (listing is not null && listing.Url == url)
            {
                outcomes.Add(new ProposeOutcome(url, "already_linked"));
                continue;
            }

            if (all.TryGetValue(url, out var known) && known.Status == CandidateStatus.Rejected)
            {
                // A person already said no to this page; it is not proposed again.
                outcomes.Add(new ProposeOutcome(url, "rejected"));
                continue;
            }

            var title = UserNames.Clean(result.Title, 300);
            var verdict = CandidateMatcher.Evaluate(facts, new CandidateFacts(title, result.Barcode));
            if (verdict is null)
            {
                outcomes.Add(new ProposeOutcome(url, "no_match"));
                continue;
            }

            var isNew = known is null;
            var row = known ?? new MatchCandidate { Id = Guid.NewGuid(), ProductId = id, Store = store, Url = url, ProposedAt = now, Status = CandidateStatus.Proposed };
            row.Title = title;
            row.Barcode = Gtin.TryNormalize(result.Barcode, out var code) ? code : row.Barcode;
            row.Basis = verdict.Basis;
            row.Score = verdict.Score;
            passed.Add((row, isNew, outcomes.Count));
            outcomes.Add(new ProposeOutcome(url, "proposed"));
        }

        // The open proposals for this shop are the best few of what was already open and what just passed.
        var open = existing.Values.Concat(passed.Where(p => p.IsNew).Select(p => p.Row)).ToList();
        var keep = open.OrderByDescending(c => c.Score).ThenBy(c => c.ProposedAt).ThenBy(c => c.Url, StringComparer.Ordinal)
            .Take(MaxProposalsPerStore).ToHashSet();
        foreach (var (row, isNew, index) in passed)
        {
            if (!keep.Contains(row))
            {
                outcomes[index] = outcomes[index] with { Outcome = "outranked" };
            }
            else if (isNew)
            {
                db.MatchCandidates.Add(row);
            }
        }

        db.MatchCandidates.RemoveRange(existing.Values.Where(c => !keep.Contains(c)));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two posts for the same product and shop raced on the same page; the other one won, so ask again.
            return Results.Json(new { error = "conflict", message = "Another post for this product was saving; send it again." }, statusCode: 409);
        }

        return Results.Ok(new { proposed = outcomes.Count(o => o.Outcome == "proposed"), results = outcomes });
    }

    /// <summary>
    /// A person says an open proposal is the product's equivalent in that shop: the shop's listing now points at it and is
    /// confirmed (the same as pasting the page), and the shop's other open proposals are settled. Rejected rows stay.
    /// </summary>
    private static async Task<IResult> Confirm(Guid id, Guid candidateId, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var candidate = await db.MatchCandidates.SingleOrDefaultAsync(c => c.Id == candidateId && c.ProductId == id && c.Status == CandidateStatus.Proposed, ct);
        if (candidate is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var listing = await db.StoreListings.SingleOrDefaultAsync(l => l.ProductId == id && l.Store == candidate.Store, ct);
        listing = await ProductEndpoints.PointListingAsync(db, listing, id, candidate.Store, candidate.Url, now, ct);
        listing.ConfirmedAt = now;
        await db.MatchCandidates.Where(c => c.ProductId == id && c.Store == candidate.Store && c.Status == CandidateStatus.Proposed).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ListingResponse(listing.Url, listing.Title, listing.LastCheckedAt, listing.LastError, listing.Category, listing.ImageUrl, true));
    }

    /// <summary>A person says an open proposal is not the equivalent. It is kept as rejected so it never comes back.</summary>
    private static async Task<IResult> Reject(Guid id, Guid candidateId, AppDbContext db, CancellationToken ct)
    {
        var candidate = await db.MatchCandidates.SingleOrDefaultAsync(c => c.Id == candidateId && c.ProductId == id && c.Status == CandidateStatus.Proposed, ct);
        if (candidate is null)
        {
            return Results.NotFound();
        }

        candidate.Status = CandidateStatus.Rejected;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetBarcode(Guid id, SetBarcodeRequest request, AppDbContext db, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        string? barcode = null;
        if (!string.IsNullOrWhiteSpace(request.Barcode) && !Gtin.TryNormalize(request.Barcode, out barcode))
        {
            return Results.Json(new { error = "barcode_invalid", message = "That is not a valid barcode (8, 12, 13 or 14 digits with a correct check digit)." }, statusCode: 400);
        }

        if (barcode is not null && await db.Products.AnyAsync(p => p.Barcode == barcode && p.Id != id, ct))
        {
            return BarcodeExists();
        }

        product.Barcode = barcode;

        var facts = new ProductFacts(product.Name, barcode);
        foreach (var candidate in await db.MatchCandidates.Where(c => c.ProductId == id && c.Status == CandidateStatus.Proposed).ToListAsync(ct))
        {
            var verdict = CandidateMatcher.Evaluate(facts, new CandidateFacts(candidate.Title, candidate.Barcode));
            if (verdict is null)
            {
                db.MatchCandidates.Remove(candidate);
                continue;
            }

            candidate.Basis = verdict.Basis;
            candidate.Score = verdict.Score;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return BarcodeExists();
        }

        return Results.Ok(new { barcode });
    }

    internal static IResult BarcodeExists() =>
        Results.Json(new { error = "barcode_exists", message = "Another product already has that barcode." }, statusCode: 409);
}
