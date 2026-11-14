using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Matching;

/// <summary>
/// How a product is looked up in one shop's catalog and how a catalog row is shown to the matcher: shared by the server's candidate
/// search (<see cref="ServerCandidates"/>) and the equivalents answer, so the two can never disagree about which rows are worth
/// comparing or what a row is called.
/// <para>
/// A row is worth comparing when it has the product's barcode, or when it states the product's size (amount, unit and pack count)
/// and its name holds the product's first generic word (the kind of thing it is). A product with neither a barcode nor a size is
/// matched by nobody: with tens of thousands of rows there is no cheap, safe way to narrow without one.
/// </para>
/// </summary>
public static class CatalogRows
{
    /// <summary>Most catalog rows one product is compared with per shop; the matcher is cheap, the cap is for a name every row shares.</summary>
    public const int MaxRowsPerStore = 300;

    /// <summary>The live (not discontinued) rows of a shop worth comparing with the product, in a fixed order.</summary>
    public static async Task<List<StoreProduct>> NarrowAsync(AppDbContext db, Store store, ProductFacts facts, CancellationToken ct)
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
        if (size is not null && CandidateMatcher.KeyWord(facts.Name) is { } word)
        {
            var unit = size.Unit switch
            {
                SizeUnit.Mass => StoreProduct.Grams,
                SizeUnit.Volume => StoreProduct.Millilitres,
                _ => StoreProduct.Each,
            };
            var bySize = shop.Where(p => p.SizeUnit == unit && p.SizeAmount == size.Amount && p.SizePacks == size.Packs);
            // The word is letters and digits only (the matcher's folding), so they carry no LIKE syntax.
            var like = $"%{word}%";
            foreach (var row in await bySize
                         .Where(p => EF.Functions.ILike(p.Name, like))
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
    public static string TitleOf(StoreProduct row) =>
        PriceRecorder.Truncate(
            row.Brand is { Length: > 0 } brand && !row.Name.Contains(brand, StringComparison.OrdinalIgnoreCase) ? $"{brand} {row.Name}" : row.Name,
            300)!;

    /// <summary>The size the row states in its own fields; null when the shop's text stated none.</summary>
    public static ProductSize? SizeOf(StoreProduct row) =>
        row.SizeAmount is { } amount && row.SizeUnit is { } unit
            ? new ProductSize(unit == StoreProduct.Grams ? SizeUnit.Mass : unit == StoreProduct.Millilitres ? SizeUnit.Volume : SizeUnit.Count, amount, row.SizePacks)
            : null;

    /// <summary>What the matcher knows of a catalog row taken as the product to find equivalents of.</summary>
    public static ProductFacts FactsOf(StoreProduct row) => new(TitleOf(row), row.Barcode, row.Category, SizeOf(row));
}
