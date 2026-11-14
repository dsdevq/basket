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
/// states the product's size (amount, unit and pack count) and its name holds the first or the last generic word of the product's
/// (the kind of thing it is). A product with neither a barcode nor a size is matched by nobody here: the endpoint still takes a
/// search for it. Run when a product is added, when a page is confirmed (the product has learned a barcode, size and category) and when
/// a sweep of a shop is completed (new rows to look at), the last one in batches of <see cref="BatchSize"/> products.
/// </para>
/// </summary>
public sealed class ServerCandidates(AppDbContext db, PriceRecorder prices, TimeProvider clock, ILogger<ServerCandidates> log)
{
    /// <summary>Products loaded and matched together in a sweep-wide run.</summary>
    public const int BatchSize = 25;

    /// <summary>Most catalog rows one product is compared with per shop; the matcher is cheap, the cap is for a name every row shares.</summary>
    public const int MaxRowsPerStore = 300;

    /// <summary>Matches one product against the catalog of every shop where it has no confirmed page.</summary>
    public Task<ServerCandidateResult> RunForProductAsync(Guid productId, CancellationToken ct) => RunAsync([productId], null, ct);

    /// <summary>Matches every product with an open gap in <paramref name="store"/> against that shop's catalog: a sweep of it has just closed.</summary>
    public async Task<ServerCandidateResult> RunForStoreAsync(Store store, CancellationToken ct)
    {
        var ids = await db.Products.AsNoTracking()
            .Where(p => !db.StoreListings.Any(l => l.ProductId == p.Id && l.Store == store && l.ConfirmedAt != null))
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);
        return await RunAsync(ids, store, ct);
    }

    private async Task<ServerCandidateResult> RunAsync(IReadOnlyList<Guid> productIds, Store? only, CancellationToken ct)
    {
        var total = new ServerCandidateResult(0, 0, 0);
        foreach (var batch in productIds.Chunk(BatchSize))
        {
            var products = await db.Products.Where(p => batch.Contains(p.Id)).OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(ct);
            var listings = (await db.StoreListings.Where(l => batch.Contains(l.ProductId)).ToListAsync(ct)).ToLookup(l => l.ProductId);
            var linkedIds = listings.SelectMany(g => g).Where(l => l.ConfirmedAt != null && l.StoreProductId != null).Select(l => l.StoreProductId!.Value).ToList();
            var linked = await db.StoreProducts.AsNoTracking().Where(p => linkedIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

            foreach (var product in products)
            {
                try
                {
                    var result = await MatchAsync(product, listings[product.Id].ToList(), linked, only, ct);
                    await db.SaveChangesAsync(ct);
                    total += result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One product's failure (two runs racing on the same page, say) must not stop the others, nor fail the request
                    // that triggered the run: a proposal is a convenience, the next trigger tries again.
                    log.LogWarning(ex, "Candidate matching failed for product {ProductId}", product.Id);
                    db.ChangeTracker.Clear();
                }
            }
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
        Store? only,
        CancellationToken ct)
    {
        var facts = FactsOf(product, listings, linked);
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
            foreach (var row in await RowsAsync(store, facts, ct))
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

                var title = TitleOf(row);
                var verdict = CandidateMatcher.Evaluate(facts, new CandidateFacts(title, row.Barcode, row.Category, SizeOf(row)));
                if (verdict is not null)
                {
                    passed.Add((row, new Proposal(url, title, row.Barcode, verdict)));
                }
                else if (known is not null)
                {
                    // The catalog's own reading of an open proposal no longer matches (the product learned a barcode or category since):
                    // it is withdrawn, as changing a product's barcode withdraws what can no longer match.
                    db.MatchCandidates.Remove(known);
                    all.Remove(url);
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
        var size = ProductSize.Parse(product.Name) is null ? rows.Select(SizeOf).FirstOrDefault(s => s is not null) : null;
        return new ProductFacts(product.Name, barcode, category, size);
    }

    /// <summary>The catalog rows of a shop worth comparing with the product (see the class summary), live ones only, in a fixed order.</summary>
    private async Task<List<StoreProduct>> RowsAsync(Store store, ProductFacts facts, CancellationToken ct)
    {
        var shop = db.StoreProducts.AsNoTracking().Where(p => p.Store == store && p.DiscontinuedAt == null);
        var rows = new Dictionary<Guid, StoreProduct>();
        if (Gtin.TryNormalize(facts.Barcode, out var code))
        {
            foreach (var row in await shop.Where(p => p.Barcode == code).OrderBy(p => p.Name).ThenBy(p => p.Id).Take(MaxRowsPerStore).ToListAsync(ct))
            {
                rows[row.Id] = row;
            }
        }

        var size = facts.Size ?? ProductSize.Parse(facts.Name);
        var words = CandidateMatcher.KeyWords(facts.Name);
        if (size is not null && words.Count > 0)
        {
            var unit = size.Unit switch
            {
                SizeUnit.Mass => StoreProduct.Grams,
                SizeUnit.Volume => StoreProduct.Millilitres,
                _ => StoreProduct.Each,
            };
            var bySize = shop.Where(p => p.SizeUnit == unit && p.SizeAmount == size.Amount && p.SizePacks == size.Packs);
            // The words are letters and digits only (the matcher's folding), so they carry no LIKE syntax.
            var first = $"%{words[0]}%";
            var last = $"%{words[^1]}%";
            foreach (var row in await bySize
                         .Where(p => EF.Functions.ILike(p.Name, first) || EF.Functions.ILike(p.Name, last) || (p.Brand != null && (EF.Functions.ILike(p.Brand + " " + p.Name, first) || EF.Functions.ILike(p.Brand + " " + p.Name, last))))
                         .OrderBy(p => p.Name).ThenBy(p => p.Id).Take(MaxRowsPerStore).ToListAsync(ct))
            {
                rows[row.Id] = row;
            }
        }

        return rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
    }

    /// <summary>
    /// The name a row is compared and shown under: the brand first when the name does not already carry it, because a brand is what
    /// tells "Heinz baked beans" from "Branston baked beans" and the shops keep it in a field of its own.
    /// </summary>
    private static string TitleOf(StoreProduct row) =>
        PriceRecorder.Truncate(
            row.Brand is { Length: > 0 } brand && !row.Name.Contains(brand, StringComparison.OrdinalIgnoreCase) ? $"{brand} {row.Name}" : row.Name,
            300)!;

    private static ProductSize? SizeOf(StoreProduct row) =>
        row.SizeAmount is { } amount && row.SizeUnit is { } unit
            ? new ProductSize(unit == StoreProduct.Grams ? SizeUnit.Mass : unit == StoreProduct.Millilitres ? SizeUnit.Volume : SizeUnit.Count, amount, row.SizePacks)
            : null;

    /// <summary>
    /// Makes the barcode-exact row the product's page in <paramref name="store"/>, unconfirmed, and gives it the row's latest reading so
    /// it shows a price before the next sweep (the sweep mirrors the same row from then on).
    /// </summary>
    private async Task LinkAsync(Product product, Store store, StoreProduct row, string url, DateTimeOffset now, CancellationToken ct)
    {
        var listing = await ProductEndpoints.PointListingAsync(db, null, product.Id, store, url, now, ct);
        listing.Title = PriceRecorder.Truncate(TitleOf(row), 300);
        listing.ImageUrl = row.ImageUrl;
        listing.Category = PriceRecorder.Truncate(row.Category, StoreListing.MaxCategoryLength);

        var price = await db.StoreProductPrices.AsNoTracking().Where(p => p.StoreProductId == row.Id).OrderByDescending(p => p.ObservedOn).FirstOrDefaultAsync(ct);
        if (price is not null && (price.Price is not null || price.LoyaltyPrice is not null || !price.InStock))
        {
            await prices.RecordAsync(
                listing,
                new PriceQuote(TitleOf(row), price.Price, price.WasPrice, price.LoyaltyPrice, price.LoyaltyScheme, price.PromoText, price.ValidFrom, price.ValidUntil, price.InStock, row.ImageUrl, row.Category),
                row.LastSeenAt,
                ct);
        }
    }
}
