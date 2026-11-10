using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Basket.Api.LidlPlus;

/// <summary>How Lidl answered a receipts request. Anything that is not a clean answer is <see cref="Unavailable"/>; the token is never blamed here.</summary>
public enum LidlTicketsOutcome
{
    Ok,

    /// <summary>Lidl could not be asked, refused the request, or answered in a way basket cannot read as a list.</summary>
    Unavailable,

    /// <summary>One receipt came back in a shape basket cannot read (no date, no lines). It is skipped, the rest are still read.</summary>
    Unreadable,
}

/// <summary>One entry of the receipts list: enough to know whether the receipt is already filed.</summary>
public sealed record LidlReceiptSummary(string Id);

public sealed record LidlReceiptPage(LidlTicketsOutcome Outcome, IReadOnlyList<LidlReceiptSummary> Receipts)
{
    public static LidlReceiptPage Unavailable { get; } = new(LidlTicketsOutcome.Unavailable, []);
}

/// <summary>One printed line. <see cref="Amount"/> is what the line came to; <see cref="UnitPrice"/> the shelf price of one (a kilo for a weighed line).</summary>
public sealed record LidlReceiptLine(string Description, decimal Quantity, bool IsWeight, decimal? UnitPrice, decimal? Amount);

public sealed record LidlReceipt(string Id, DateTimeOffset Date, decimal? Total, IReadOnlyList<LidlReceiptLine> Lines);

public sealed record LidlReceiptResult(LidlTicketsOutcome Outcome, LidlReceipt? Receipt)
{
    public static LidlReceiptResult Unavailable { get; } = new(LidlTicketsOutcome.Unavailable, null);

    public static LidlReceiptResult Unreadable { get; } = new(LidlTicketsOutcome.Unreadable, null);
}

/// <summary>Lidl Plus receipts. The one door to Lidl's receipts service, so tests can stand in for Lidl. The access token is a secret: do not log it.</summary>
public interface ILidlPlusTickets
{
    /// <summary>One page of the receipts list, newest first; an empty page is the end of the list. Pages count from 1.</summary>
    Task<LidlReceiptPage> ListAsync(string accessToken, int page, CancellationToken ct);

    Task<LidlReceiptResult> GetAsync(string accessToken, string id, CancellationToken ct);
}

/// <summary>
/// Reads the receipts list and one receipt from Lidl Plus's receipts service, the way the app does (a bearer access token and the
/// app's own headers). Nothing here logs a body, a header or an exception message: every failure is reduced to an outcome. Amounts
/// arrive as numbers or as text with a decimal comma; both are read.
/// </summary>
public sealed class LidlPlusTicketsClient(HttpClient http, IOptions<LidlPlusOptions> options) : ILidlPlusTickets
{
    public async Task<LidlReceiptPage> ListAsync(string accessToken, int page, CancellationToken ct)
    {
        var body = await GetBodyAsync($"api/v2/{Country}/tickets?pageNumber={page.ToString(CultureInfo.InvariantCulture)}&onlyFavorite=false", accessToken, ct);
        if (body is null)
        {
            return LidlReceiptPage.Unavailable;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tickets", out var tickets)
                || tickets.ValueKind != JsonValueKind.Array)
            {
                return LidlReceiptPage.Unavailable;
            }

            var summaries = tickets.EnumerateArray()
                .Select(t => Text(t, "id"))
                .OfType<string>()
                .Select(id => new LidlReceiptSummary(id))
                .ToList();
            return new LidlReceiptPage(LidlTicketsOutcome.Ok, summaries);
        }
        catch (JsonException)
        {
            return LidlReceiptPage.Unavailable;
        }
    }

    public async Task<LidlReceiptResult> GetAsync(string accessToken, string id, CancellationToken ct)
    {
        var body = await GetBodyAsync($"api/v2/{Country}/tickets/{Uri.EscapeDataString(id)}", accessToken, ct);
        if (body is null)
        {
            return LidlReceiptResult.Unavailable;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return Parse(id, document.RootElement);
        }
        catch (JsonException)
        {
            return LidlReceiptResult.Unreadable;
        }
    }

    private string Country => Uri.EscapeDataString(options.Value.Country);

    private static LidlReceiptResult Parse(string id, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !DateTimeOffset.TryParse(Text(root, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            || !root.TryGetProperty("itemsLine", out var itemsLine)
            || itemsLine.ValueKind != JsonValueKind.Array)
        {
            return LidlReceiptResult.Unreadable;
        }

        var lines = new List<LidlReceiptLine>();
        foreach (var line in itemsLine.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object || Text(line, "description") is not { } description)
            {
                continue;
            }

            lines.Add(new LidlReceiptLine(
                description,
                Amount(line, "quantity") ?? 1m,
                line.TryGetProperty("isWeight", out var weight) && weight.ValueKind == JsonValueKind.True,
                Amount(line, "currentUnitPrice"),
                Amount(line, "extendedAmount") ?? Amount(line, "originalAmount")));
        }

        return new LidlReceiptResult(LidlTicketsOutcome.Ok, new LidlReceipt(id, date, Amount(root, "totalAmount"), lines));
    }

    /// <summary>The body of a successful answer, or null for anything else (refused, throttled, down, unreachable).</summary>
    private async Task<string?> GetBodyAsync(string path, string accessToken, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept-Language", settings.Language);
        request.Headers.TryAddWithoutValidation("App", "com.lidl.eapp");
        request.Headers.TryAddWithoutValidation("Operating-System", "iOS");
        request.Headers.TryAddWithoutValidation("App-Version", "999.99.9");

        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A number, or text such as <c>"1,49"</c> or <c>"1.234,56"</c>; null when it is neither.</summary>
    private static decimal? Amount(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDecimal(out var number) ? number : null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            return null;
        }

        text = text.Trim();
        if (text.Contains(',', StringComparison.Ordinal))
        {
            // A comma is the decimal mark; a dot beside it is a thousands separator.
            text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
