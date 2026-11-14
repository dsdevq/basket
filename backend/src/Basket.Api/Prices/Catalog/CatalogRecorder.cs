using Basket.Api.Data;
using Basket.Api.Matching;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Catalog;

/// <summary>How a batch changed the catalog.</summary>
public sealed record CatalogRecordResult(int Created, int Updated, int PriceRows);

/// <summary>
/// The one way a product enters the combined catalog, whoever read it: the server's own sweeps call it in process, the ingest
/// endpoint calls it for the PC reader. Upserts by <c>(store, external_id)</c>, stamps <c>last_seen_at</c>, and writes a
/// <see cref="StoreProductPrice"/> row only when the price tuple differs from the latest one. Applies to the context; the caller saves.
/// </summary>
public sealed class CatalogRecorder(AppDbContext db)
{
    /// <summary>
    /// Records <paramref name="readings"/> (all of <paramref name="store"/>) as seen at <paramref name="now"/>. A product seen
    /// again is updated: its name, size, unit price and address follow the shop, while a barcode, photo, brand or category
    /// the shop did not state this time are kept, so a thinner reading never erases what an earlier one knew. A product that
    /// had been marked discontinued and is listed again comes back.
    /// </summary>
    public async Task<CatalogRecordResult> RecordAsync(Store store, IReadOnlyList<CatalogReading> readings, DateTimeOffset now, CancellationToken ct)
    {
        if (readings.Count == 0)
        {
            return new CatalogRecordResult(0, 0, 0);
        }

        var ids = readings.Select(r => r.ExternalId).Distinct().ToList();
        var products = (await db.StoreProducts.Where(p => p.Store == store && ids.Contains(p.ExternalId)).ToListAsync(ct))
            .ToDictionary(p => p.ExternalId, StringComparer.Ordinal);

        var productIds = products.Values.Select(p => p.Id).ToList();
        var latest = (await db.StoreProductPrices
                .Where(x => productIds.Contains(x.StoreProductId)
                    && x.ObservedOn == db.StoreProductPrices.Where(y => y.StoreProductId == x.StoreProductId).Max(y => y.ObservedOn))
                .ToListAsync(ct))
            .ToDictionary(x => x.StoreProductId);

        var day = PriceRecorder.DublinDay(now);
        int created = 0, updated = 0, priceRows = 0;
        foreach (var reading in readings)
        {
            if (products.TryGetValue(reading.ExternalId, out var product))
            {
                updated++;
            }
            else
            {
                product = new StoreProduct { Id = Guid.NewGuid(), Store = store, ExternalId = reading.ExternalId, FirstSeenAt = now };
                db.StoreProducts.Add(product);
                products[reading.ExternalId] = product;
                created++;
            }

            Apply(product, reading, now);

            latest.TryGetValue(product.Id, out var current);
            if (Same(current, reading.Price))
            {
                continue;
            }

            if (current is not null && current.ObservedOn == day)
            {
                // A second, different reading on the same Dublin day replaces that day's row rather than colliding with it.
                Fill(current, reading.Price);
            }
            else
            {
                var row = new StoreProductPrice { StoreProductId = product.Id, ObservedOn = day };
                Fill(row, reading.Price);
                db.StoreProductPrices.Add(row);
                latest[product.Id] = row;
            }

            priceRows++;
        }

        return new CatalogRecordResult(created, updated, priceRows);
    }

    private static void Apply(StoreProduct product, CatalogReading reading, DateTimeOffset now)
    {
        product.Url = reading.Url;
        product.Name = reading.Name;
        product.Brand = reading.Brand ?? product.Brand;
        product.SizeText = reading.SizeText;
        product.SizeAmount = reading.Size?.Amount;
        product.SizeUnit = reading.Size?.Unit switch
        {
            SizeUnit.Mass => StoreProduct.Grams,
            SizeUnit.Volume => StoreProduct.Millilitres,
            SizeUnit.Count => StoreProduct.Each,
            _ => null,
        };
        product.SizePacks = reading.Size?.Packs ?? 1;
        product.UnitPrice = reading.UnitPrice;
        product.UnitPriceBasis = reading.UnitPriceBasis;
        product.Barcode = reading.Barcode ?? product.Barcode;
        product.Category = reading.Category ?? product.Category;
        product.ImageUrl = reading.ImageUrl ?? product.ImageUrl;
        product.LastSeenAt = now;
        product.DiscontinuedAt = null;
    }

    /// <summary>No latest row counts as the empty tuple, so a product that never had a price gets no row for saying nothing.</summary>
    private static bool Same(StoreProductPrice? current, CatalogPrice price) =>
        current is null
            ? price.IsEmpty
            : current.Price == price.Price
                && current.WasPrice == price.WasPrice
                && current.LoyaltyPrice == price.LoyaltyPrice
                && current.LoyaltyScheme == price.LoyaltyScheme
                && current.PromoText == price.PromoText
                && current.ValidFrom == price.ValidFrom
                && current.ValidUntil == price.ValidUntil
                && current.InStock == price.InStock;

    private static void Fill(StoreProductPrice row, CatalogPrice price)
    {
        row.Price = price.Price;
        row.WasPrice = price.WasPrice;
        row.LoyaltyPrice = price.LoyaltyPrice;
        row.LoyaltyScheme = price.LoyaltyScheme;
        row.PromoText = price.PromoText;
        row.ValidFrom = price.ValidFrom;
        row.ValidUntil = price.ValidUntil;
        row.InStock = price.InStock;
    }
}
