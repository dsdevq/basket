using Basket.Api.LidlPlus;

namespace Basket.Tests.Support;

/// <summary>
/// Lidl Plus's receipts service as a script: a list of receipts (newest first, ten to a page) and what each one holds. A receipt can be
/// made unreadable, and the list or one receipt can be made to fail, to see how a pull copes.
/// </summary>
public sealed class FakeLidlPlusTickets : ILidlPlusTickets
{
    public const int PageSize = 10;

    private readonly object _gate = new();
    private readonly List<LidlReceipt> _receipts = [];
    private readonly HashSet<string> _unreadable = [];

    /// <summary>Every access token the receipts service was shown, in order.</summary>
    public List<string> Tokens { get; } = [];

    /// <summary>The ids of the receipts opened, in order.</summary>
    public List<string> Opened { get; } = [];

    public int ListCalls { get; private set; }

    /// <summary>When set, the list fails from this page on (pages count from 1).</summary>
    public int? ListFailsFromPage { get; set; }

    /// <summary>When set, opening this receipt fails as if Lidl went away.</summary>
    public string? OpenFailsFor { get; set; }

    public FakeLidlPlusTickets Add(LidlReceipt receipt)
    {
        lock (_gate)
        {
            _receipts.Add(receipt);
        }

        return this;
    }

    public FakeLidlPlusTickets MakeUnreadable(string id)
    {
        lock (_gate)
        {
            _unreadable.Add(id);
        }

        return this;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _receipts.Clear();
            _unreadable.Clear();
            Tokens.Clear();
            Opened.Clear();
            ListCalls = 0;
            ListFailsFromPage = null;
            OpenFailsFor = null;
        }
    }

    public static LidlReceipt Receipt(string id, DateTimeOffset date, decimal? total, params LidlReceiptLine[] lines) => new(id, date, total, lines);

    public static LidlReceiptLine Line(string description, decimal amount, decimal quantity = 1m) =>
        new(description, quantity, false, quantity == 0 ? amount : amount / quantity, amount);

    public static LidlReceiptLine Weighed(string description, decimal kilos, decimal perKilo) =>
        new(description, kilos, true, perKilo, Math.Round(kilos * perKilo, 2));

    public Task<LidlReceiptPage> ListAsync(string accessToken, int page, CancellationToken ct)
    {
        lock (_gate)
        {
            Tokens.Add(accessToken);
            ListCalls++;
            if (ListFailsFromPage is { } from && page >= from)
            {
                return Task.FromResult(LidlReceiptPage.Unavailable);
            }

            var newestFirst = _receipts.OrderByDescending(r => r.Date).Skip((page - 1) * PageSize).Take(PageSize)
                .Select(r => new LidlReceiptSummary(r.Id)).ToList();
            return Task.FromResult(new LidlReceiptPage(LidlTicketsOutcome.Ok, newestFirst));
        }
    }

    public Task<LidlReceiptResult> GetAsync(string accessToken, string id, CancellationToken ct)
    {
        lock (_gate)
        {
            Tokens.Add(accessToken);
            Opened.Add(id);
            if (id == OpenFailsFor)
            {
                return Task.FromResult(LidlReceiptResult.Unavailable);
            }

            if (_unreadable.Contains(id))
            {
                return Task.FromResult(LidlReceiptResult.Unreadable);
            }

            var receipt = _receipts.SingleOrDefault(r => r.Id == id);
            return Task.FromResult(receipt is null ? LidlReceiptResult.Unavailable : new LidlReceiptResult(LidlTicketsOutcome.Ok, receipt));
        }
    }
}
