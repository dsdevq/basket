using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Matching;

/// <summary>What one run did: the products it looked at, the proposals it wrote or refreshed, the pages it linked.</summary>
public sealed record ServerCandidateResult(int Products, int Proposed, int Linked)
{
    public static ServerCandidateResult operator +(ServerCandidateResult a, ServerCandidateResult b) =>
        new(a.Products + b.Products, a.Proposed + b.Proposed, a.Linked + b.Linked);
}

/// <summary>
/// The server's own candidate search: for a shelf product and every shop where it has no confirmed page yet (an open gap), the
/// combined catalog (<see cref="StoreProduct"/>) is narrowed in SQL and each row that remains goes through <see cref="CandidateMatcher"/>,
/// the same pure function the endpoint uses for a posted search. A pass is written through <see cref="CandidateBook"/> (the same
/// cap of <see cref="MatchEndpoints.MaxProposalsPerStore"/> open proposals per shop, rejected pages never proposed again), and a
/// row with the product's own barcode becomes the shop's page directly, unconfirmed (<c>confirmed_at</c> stays null, so it is read for
/// prices like any page but a person still has the last word). Nothing here touches a confirmed page, and nothing here is a new door:
/// the catalog arrives through the existing ingest and sweeps.
/// <para>
/// Narrowing, because the catalog is tens of thousands of rows: a row is a candidate when it has the product's barcode, or when it
/// states the product's size (amount, unit and pack count) and its name holds the first generic word of the product's
/// (the kind of thing it is). A product with neither a barcode nor a size is matched by nobody here: the endpoint still takes a
/// search for it. Run when a product is added, when a page is confirmed (the product has learned a barcode, size and category) and when
/// a sweep of a shop is completed (new rows to look at), the last one in batches of <see cref="BatchSize"/> products.
/// </para>
/// </summary>
public sealed class ServerCandidates(AppDbContext db, TimeProvider clock, ILogger<ServerCandidates> log)
{
    /// <summary>Products loaded and matched together in a sweep-wide run.</summary>
    public const int BatchSize = 25;

    /// <summary>Matches one product against the catalog of every shop where it has no confirmed page.</summary>
    public Task<ServerCandidateResult> RunForProductAsync(Guid productId, CancellationToken ct) => RunAsync([productId], null, ct);

    /// <summary>Matches every product with an open gap in <paramref name="store"/> against that shop's catalog: a sweep of it has just closed.</summary>
    public Task<ServerCandidateResult> RunForStoreAsync(Store store, CancellationToken ct) => RunAsync(null, store, ct);

    private async Task<ServerCandidateResult> RunAsync(IReadOnlyList<Guid>? productIds, Store? only, CancellationToken ct)
    {
        var total = new ServerCandidateResult(0, 0, 0);
        try
        {
            productIds ??= await db.Products.AsNoTracking()
                .Where(p => !db.StoreListings.Any(l => l.ProductId == p.Id && l.Store == only && l.ConfirmedAt != null))
                .OrderBy(p => p.Name).ThenBy(p => p.Id)
                .Select(p => p.Id)
                .ToListAsync(ct);
            foreach (var batch in productIds.Chunk(BatchSize))
            {
                var products = await db.Products.Where(p => batch.Contains(p.Id)).OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(ct);
                var listings = (await db.StoreListings.Where(l => batch.Contains(l.ProductId)).ToListAsync(ct)).ToLookup(l => l.ProductId);
                var linkedIds = listings.SelectMany(g => g).Where(l => l.ConfirmedAt != null && l.StoreProductId != null).Select(l => l.StoreProductId!.Value).ToList();
                var linked = await db.StoreProducts.AsNoTracking().Where(p => linkedIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
                var aliases = await ProductAliases.TextsAsync(db, batch, ct);

                foreach (var product in products)
                {
                    try
                    {
                        var result = await MatchAsync(product, listings[product.Id].ToList(), linked, aliases[product.Id].ToList(), only, ct);
                        await db.SaveChangesAsync(ct);
                        total += result;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Candidate matching failed for product {ProductId}", product.Id);
                        db.ChangeTracker.Clear();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A proposal is a convenience: whatever fails here (a racing run, a dropped connection) must not fail the add, the confirm
            // or the closing of the sweep that triggered the run; the next trigger tries again.
            log.LogWarning(ex, "Candidate matching failed");
            db.ChangeTracker.Clear();
        }

        if (total.Proposed > 0 || total.Linked > 0)
        {
            log.LogInformation("Candidate matching over {Products} products: {Proposed} proposals, {Linked} pages linked", total.Products, total.Proposed, total.Linked);
        }

        return total;
    }

    private async Task<ServerCandidateResult> MatchAsync(
        Product product,
        List<StoreListing> listings,
        Dictionary<Guid, StoreProduct> linked,
        List<string> aliases,
        Store? only,
        CancellationToken ct)
    {
        var facts = FactsOf(product, listings, linked);
        // The product's own name, then each other name the household gave it that has English words: a row passes if any name matches it.
        var names = ProductAliases.FactsWithAliases(facts, aliases);
        var proposed = 0;
        var linkedPages = 0;
        var now = clock.GetUtcNow();
        foreach (var store in only is { } one ? [one] : Stores.All)
        {
            var listing = listings.FirstOrDefault(l => l.Store == store);
            if (listing?.ConfirmedAt is not null)
            {
                continue;
            }

            var all = await db.MatchCandidates.Where(c => c.ProductId == product.Id && c.Store == store).ToDictionaryAsync(c => c.Url, StringComparer.Ordinal, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var passed = new List<(StoreProduct Row, Proposal Proposal)>();
            foreach (var row in await NarrowAnyAsync(store, names, ct))
            {
                if (!Stores.TryNormalizeProductUrl(store, row.Url, out var uri))
                {
                    continue;
                }

                var url = uri.ToString();
                if (!seen.Add(url) || listing?.Url == url || (all.TryGetValue(url, out var known) && known.Status == CandidateStatus.Rejected))
                {
                    continue;
                }

                var title = CatalogRows.TitleOf(row);
                var verdict = CandidateMatcher.EvaluateAny(names, new CandidateFacts(title, row.Barcode, row.Category, CatalogRows.SizeOf(row)));
                if (verdict is not null)
                {
                    passed.Add((row, new Proposal(url, title, row.Barcode, verdict)));
                }
            }

            if (listing is null && passed.FirstOrDefault(p => p.Proposal.Verdict.Basis == MatchBasis.Barcode) is { Row: not null } exact)
            {
                await LinkAsync(product, store, exact.Row, exact.Proposal.Url, now, ct);
                passed.Remove(exact);
                if (all.Remove(exact.Proposal.Url, out var settled) && settled.Status == CandidateStatus.Proposed)
                {
                    // The page is the product's page now; its proposal would only ask a person about what is already linked.
                    db.MatchCandidates.Remove(settled);
                }

                linkedPages++;
            }

            var outranked = CandidateBook.Apply(db, product.Id, store, all, passed.Select(p => p.Proposal).ToList(), now);
            proposed += passed.Count(p => !outranked.Contains(p.Proposal.Url));
        }

        return new ServerCandidateResult(1, proposed, linkedPages);
    }

    /// <summary>The rows worth comparing under any of the product's names, each once, in the order <see cref="CatalogRows.NarrowAsync"/> gives them.</summary>
    private async Task<List<StoreProduct>> NarrowAnyAsync(Store store, IReadOnlyList<ProductFacts> names, CancellationToken ct)
    {
        var rows = new Dictionary<Guid, StoreProduct>();
        foreach (var name in names)
        {
            foreach (var row in await CatalogRows.NarrowAsync(db, store, name, ct))
            {
                rows.TryAdd(row.Id, row);
            }
        }

        return rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
    }

    /// <summary>
    /// What is known of the product: its name, and what its confirmed pages add (the barcode when the product has none, the size when
    /// its name states none, the category). A page a machine linked and no person confirmed is not evidence of anything.
    /// </summary>
    private static ProductFacts FactsOf(Product product, List<StoreListing> listings, Dictionary<Guid, StoreProduct> linked)
    {
        var confirmed = listings.Where(l => l.ConfirmedAt != null).OrderBy(l => l.Store).ToList();
        var rows = confirmed.Select(l => l.StoreProductId is { } id && linked.TryGetValue(id, out var row) ? row : null).Where(r => r is not null).Select(r => r!).ToList();
        var barcode = product.Barcode ?? rows.Select(r => r.Barcode).FirstOrDefault(b => b is not null);
        var category = confirmed.Select(l => l.Category).FirstOrDefault(c => c is not null) ?? rows.Select(r => r.Category).FirstOrDefault(c => c is not null);
        var size = ProductSize.Parse(product.Name) is null ? rows.Select(CatalogRows.SizeOf).FirstOrDefault(s => s is not null) : null;
        return new ProductFacts(product.Name, barcode, category, size);
    }

    /// <summary>Makes the barcode-exact row the product's page in <paramref name="store"/>, unconfirmed.</summary>
    private async Task LinkAsync(Product product, Store store, StoreProduct row, string url, DateTimeOffset now, CancellationToken ct)
    {
        var listing = await ProductEndpoints.PointListingAsync(db, null, product.Id, store, url, now, ct);
        listing.Title = PriceRecorder.Truncate(CatalogRows.TitleOf(row), 300);
        listing.ImageUrl = row.ImageUrl;
        listing.Category = PriceRecorder.Truncate(row.Category, StoreListing.MaxCategoryLength);
    }
}
