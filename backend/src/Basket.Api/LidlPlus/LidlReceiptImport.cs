using Basket.Api.Data;
using Basket.Api.Lists;
using Basket.Api.Prices;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Basket.Api.LidlPlus;

public enum ReceiptImportOutcome
{
    /// <summary>Every receipt that could be read is filed (there may have been none).</summary>
    Done,

    NotConnected,

    /// <summary>Lidl refused the saved token, or it cannot be read; the owner has to paste a new one.</summary>
    NeedsNewToken,

    /// <summary>Lidl could not be reached or stopped answering; what was filed before that stays, the rest waits for the next pull.</summary>
    Unavailable,

    /// <summary>There is no owner to record the new products against.</summary>
    NoOwner,
}

/// <param name="Receipts">Receipts filed as trips by this pull.</param>
/// <param name="Products">Products added to the shelf by this pull.</param>
/// <param name="Skipped">Receipts Lidl refused or basket could not read, left for the next pull. They do not use up the pull's limit.</param>
/// <param name="Waiting">New receipts left unopened because the pull's limit was reached or Lidl stopped answering; the next pull takes them.</param>
public sealed record ReceiptImportResult(ReceiptImportOutcome Outcome, int Receipts, int Products, int Skipped, int Waiting)
{
    public static ReceiptImportResult Stopped(ReceiptImportOutcome outcome) => new(outcome, 0, 0, 0, 0);
}

/// <summary>
/// Pulls the household's Lidl Plus receipts in, the way the receipts list of the Lidl Plus app shows them: each receipt becomes a
/// closed trip on the day it was printed, with its lines. A line is matched to a product by its name, letter case aside, and added
/// to the shelf when no product has that name. The price paid for each product is kept with its day (the plan and the Prices page
/// read the latest). A receipt that is already filed is never opened again, so a pull can run any number of times.
/// </summary>
public sealed class LidlReceiptImport(
    AppDbContext db,
    LidlPlusConnectionService connection,
    ILidlPlusTickets tickets,
    IOptions<LidlPlusOptions> options,
    TimeProvider clock,
    ILogger<LidlReceiptImport> log)
{
    // One household, one app instance: a pull that finds another running waits for it and then finds its receipts filed.
    private static readonly SemaphoreSlim Pulling = new(1, 1);

    public async Task<ReceiptImportResult> PullAsync(CancellationToken ct)
    {
        await Pulling.WaitAsync(ct);
        try
        {
            return await PullCoreAsync(ct);
        }
        finally
        {
            Pulling.Release();
        }
    }

    private async Task<ReceiptImportResult> PullCoreAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var access = await connection.AccessAsync(ct);
        switch (access.State)
        {
            case LidlPlusState.NotConnected:
                return ReceiptImportResult.Stopped(ReceiptImportOutcome.NotConnected);
            case LidlPlusState.NeedsNewToken or LidlPlusState.Unreadable:
                return ReceiptImportResult.Stopped(ReceiptImportOutcome.NeedsNewToken);
        }

        if (access.AccessToken is not { } accessToken)
        {
            return ReceiptImportResult.Stopped(ReceiptImportOutcome.Unavailable);
        }

        if (await ProductEndpoints.OwnerIdAsync(db, ct) is not { } owner)
        {
            return ReceiptImportResult.Stopped(ReceiptImportOutcome.NoOwner);
        }

        var filed = (await db.Trips.AsNoTracking()
                .Where(t => t.Source == TripSources.LidlPlus && t.ExternalId != null)
                .Select(t => t.ExternalId!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // The list is newest first. Every page is read (a page of filed receipts says nothing about the older ones, which a
        // capped earlier pull may have left); only receipts not yet filed are opened, until the limit of readable ones is reached.
        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= settings.MaxReceiptPages; page++)
        {
            var list = await tickets.ListAsync(accessToken, page, ct);
            if (list.Outcome != LidlTicketsOutcome.Ok)
            {
                log.LogWarning("Lidl Plus receipts list page {Page} could not be read; nothing more is pulled this time", page);
                return await FileAsync(pending, ReceiptImportOutcome.Unavailable, accessToken, owner, ct);
            }

            if (list.Receipts.Count == 0)
            {
                break;
            }

            pending.AddRange(list.Receipts.Select(r => r.Id).Where(id => !filed.Contains(id) && seen.Add(id)));

            await PauseAsync(ct);
        }

        return await FileAsync(pending, ReceiptImportOutcome.Done, accessToken, owner, ct);
    }

    private async Task<ReceiptImportResult> FileAsync(
        IReadOnlyList<string> pending,
        ReceiptImportOutcome outcome,
        string accessToken,
        Guid owner,
        CancellationToken ct)
    {
        var receipts = new List<LidlReceipt>();
        var skipped = 0;
        var waiting = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            if (receipts.Count >= options.Value.MaxNewReceiptsPerPull)
            {
                waiting = pending.Count - i;
                break;
            }

            await PauseAsync(ct);
            var result = await tickets.GetAsync(accessToken, pending[i], ct);
            if (result.Outcome == LidlTicketsOutcome.Unavailable)
            {
                outcome = ReceiptImportOutcome.Unavailable;
                waiting = pending.Count - i;
                break;
            }

            if (result.Receipt is { } receipt)
            {
                receipts.Add(receipt);
            }
            else
            {
                skipped++;
                log.LogWarning("A Lidl Plus receipt could not be read and was left for the next pull");
            }
        }

        var products = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in await db.Products.AsNoTracking().OrderBy(p => p.AddedAt).ThenBy(p => p.Id).ToListAsync(ct))
        {
            products.TryAdd(product.Name, product);
        }

        var added = 0;
        var productsAdded = 0;
        foreach (var receipt in receipts)
        {
            var (filedNow, newProducts) = await FileReceiptAsync(receipt, products, owner, ct);
            added += filedNow ? 1 : 0;
            productsAdded += newProducts;
        }

        log.LogInformation(
            "Lidl Plus receipts: {Outcome}, {Receipts} filed, {Products} products added, {Skipped} unreadable, {Waiting} waiting",
            outcome,
            added,
            productsAdded,
            skipped,
            waiting);
        return new ReceiptImportResult(outcome, added, productsAdded, skipped, waiting);
    }

    /// <summary>
    /// Files one receipt (trip, lines, new products, paid prices) in a single save, so it is there whole or not at all. A save that
    /// loses a race to another pull is looked at again: if that pull filed this receipt its copy stands, if it only took the day's
    /// paid price, the receipt is built again against what is there now.
    /// </summary>
    private async Task<(bool Filed, int NewProducts)> FileReceiptAsync(
        LidlReceipt receipt,
        Dictionary<string, Product> products,
        Guid owner,
        CancellationToken ct)
    {
        var externalId = PriceRecorder.Truncate(receipt.Id, 64);
        for (var attempt = 0; ; attempt++)
        {
            var newProducts = await StageReceiptAsync(receipt, externalId, products, owner, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return (true, newProducts.Count);
            }
            catch (DbUpdateException ex) when (ListEndpoints.IsUniqueViolation(ex))
            {
                // The products made for this receipt are not in the database.
                db.ChangeTracker.Clear();
                foreach (var product in newProducts)
                {
                    products.Remove(product.Name);
                }

                if (attempt == 1 || await db.Trips.AnyAsync(t => t.Source == TripSources.LidlPlus && t.ExternalId == externalId, ct))
                {
                    return (false, 0);
                }
            }
        }
    }

    private async Task<List<Product>> StageReceiptAsync(
        LidlReceipt receipt,
        string? externalId,
        Dictionary<string, Product> products,
        Guid owner,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            ClosedAt = receipt.Date,
            Source = TripSources.LidlPlus,
            ExternalId = externalId,
            Total = receipt.Total is { } total and >= 0 and < 100_000_000m ? Math.Round(total, 2) : null,
        };
        var paidOn = PriceRecorder.DublinDay(receipt.Date);
        var newProducts = new List<Product>();
        var bought = new Dictionary<Guid, decimal>();
        var items = new List<TripItem>();

        // A line scanned twice is one line of the trip; the paid price is the last scan's.
        foreach (var line in receipt.Lines.Where(IsPurchase))
        {
            var name = UserNames.Clean(line.Description, Product.MaxNameLength);
            if (name is null)
            {
                continue;
            }

            if (!products.TryGetValue(name, out var product) && products.Count < ProductEndpoints.MaxProducts)
            {
                product = new Product { Id = Guid.NewGuid(), Name = name, AddedAt = now, AddedByUserId = owner };
                products[name] = product;
                newProducts.Add(product);
            }

            if (items.Any(i => string.Equals(i.Text, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (product is not null)
                {
                    bought[product.Id] = PaidEach(line);
                }

                continue;
            }

            items.Add(new TripItem
            {
                Id = Guid.NewGuid(),
                TripId = trip.Id,
                Text = name,
                CheckedAt = receipt.Date,
                ProductId = product?.Id,
                Store = product is null ? null : Store.Lidl,
            });
            if (product is not null)
            {
                bought[product.Id] = PaidEach(line);
            }
        }

        db.Products.AddRange(newProducts);
        db.Trips.Add(trip);
        db.TripItems.AddRange(items);

        var boughtIds = bought.Keys.ToList();
        var sameDay = await db.PaidPrices
            .Where(p => p.Store == Store.Lidl && p.PaidOn == paidOn && boughtIds.Contains(p.ProductId))
            .ToDictionaryAsync(p => p.ProductId, ct);
        foreach (var (productId, price) in bought)
        {
            if (sameDay.TryGetValue(productId, out var existing))
            {
                if (existing.PaidAt is not { } at || receipt.Date > at)
                {
                    existing.Price = price;
                    existing.PaidAt = receipt.Date;
                }
            }
            else
            {
                db.PaidPrices.Add(new PaidPrice { Id = Guid.NewGuid(), ProductId = productId, Store = Store.Lidl, PaidOn = paidOn, Price = price, PaidAt = receipt.Date });
            }
        }

        return newProducts;
    }

    /// <summary>A line that was bought: it has a name and came to money. Deposits returned, refunds and discount lines are negative or zero and are not.</summary>
    private static bool IsPurchase(LidlReceiptLine line) =>
        !string.IsNullOrWhiteSpace(line.Description) && line.Amount is > 0 && line.Quantity > 0;

    /// <summary>What one cost: the line's amount over its quantity, or the price per kilo for a weighed line.</summary>
    private static decimal PaidEach(LidlReceiptLine line)
    {
        var each = line is { IsWeight: true, UnitPrice: > 0 } ? line.UnitPrice!.Value : line.Amount!.Value / line.Quantity;
        return Math.Min(Math.Round(each, 2), 99_999_999m);
    }

    private Task PauseAsync(CancellationToken ct) =>
        options.Value.ReceiptPauseMilliseconds > 0 ? Task.Delay(options.Value.ReceiptPauseMilliseconds, ct) : Task.CompletedTask;
}

/// <summary>The nightly receipts pull. Nothing to do (and nothing logged as a problem) until the owner has connected Lidl Plus.</summary>
public sealed class LidlReceiptsJob(LidlReceiptImport import, ILogger<LidlReceiptsJob> log)
{
    public const string Id = "lidl-receipts";

    public async Task RunAsync(CancellationToken ct)
    {
        var result = await import.PullAsync(ct);
        if (result.Outcome is ReceiptImportOutcome.NeedsNewToken or ReceiptImportOutcome.Unavailable or ReceiptImportOutcome.NoOwner)
        {
            log.LogWarning("The nightly Lidl Plus receipts pull stopped: {Outcome}", result.Outcome);
        }
    }

    public static void Schedule(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<LidlReceiptsJob>(
            Id,
            job => job.RunAsync(CancellationToken.None),
            "0 5 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin") });
}
