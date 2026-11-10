using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Basket.Api.Cards;

public sealed record SaveCardRequest(string? Number, string? Format);

/// <summary>A card in the list: the number is hidden down to its last four characters.</summary>
public sealed record CardSummary(string Store, string Name, string Format, string Masked, bool Readable, DateTimeOffset UpdatedAt);

/// <summary>One card with its number, for drawing the code. Only the single-card route returns this.</summary>
public sealed record CardResponse(string Store, string Name, string Format, string? Number, DateTimeOffset UpdatedAt);

/// <summary>
/// The household's loyalty cards, one per shop. Card numbers are personal data: they are encrypted before they reach the
/// database, returned whole only by the single-card route, never written to a log, and never cached.
/// </summary>
public static class CardEndpoints
{
    private const string StoreIndex = "IX_loyalty_card_store";

    public static void MapCardEndpoints(this IEndpointRouteBuilder api)
    {
        var cards = api.MapGroup("/cards").AddEndpointFilter(NoStore);
        cards.MapGet("/", GetCards);
        cards.MapGet("/{store}", GetCard);
        cards.MapPut("/{store}", SaveCard);
        cards.MapDelete("/{store}", DeleteCard);
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static async Task<IResult> GetCards(AppDbContext db, CardNumberProtector protector, CancellationToken ct)
    {
        var cards = await db.LoyaltyCards.AsNoTracking().ToListAsync(ct);
        var summaries = cards
            .OrderBy(c => c.Store)
            .Select(c =>
            {
                var number = protector.Unprotect(c.NumberProtected);
                return new CardSummary(
                    Key(c.Store),
                    c.Store.DisplayName(),
                    CardNumbers.FormatName(c.Format),
                    number is null ? string.Empty : CardNumbers.Mask(number),
                    number is not null,
                    c.UpdatedAt);
            });
        return Results.Ok(new { cards = summaries });
    }

    private static async Task<IResult> GetCard(string store, AppDbContext db, CardNumberProtector protector, CancellationToken ct)
    {
        if (!Stores.TryParse(store, out var shop))
        {
            return UnknownStore();
        }

        var card = await db.LoyaltyCards.AsNoTracking().SingleOrDefaultAsync(c => c.Store == shop, ct);
        return card is null
            ? Results.NotFound()
            : Results.Ok(new CardResponse(Key(shop), shop.DisplayName(), CardNumbers.FormatName(card.Format), protector.Unprotect(card.NumberProtected), card.UpdatedAt));
    }

    /// <summary>Adds the shop's card, or replaces the number and code of the one already there. One card per shop.</summary>
    private static async Task<IResult> SaveCard(
        string store,
        SaveCardRequest request,
        AppDbContext db,
        CardNumberProtector protector,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!Stores.TryParse(store, out var shop))
        {
            return UnknownStore();
        }

        var format = CardNumbers.DefaultFormat(shop);
        if (request.Format is not null && !CardNumbers.TryParseFormat(request.Format, out format))
        {
            return Results.Json(
                new { error = "format_unknown", message = "Choose Code 128 or QR code." }, statusCode: 400);
        }

        // The message never repeats what was typed: a refused number is still a card number.
        var number = CardNumbers.Normalize(format, request.Number);
        if (number is null)
        {
            return Results.Json(new { error = "number_invalid", message = NumberHint(format) }, statusCode: 400);
        }

        var now = clock.GetUtcNow();
        var card = await db.LoyaltyCards.SingleOrDefaultAsync(c => c.Store == shop, ct);
        var created = card is null;
        if (card is null)
        {
            card = new LoyaltyCard
            {
                Id = Guid.NewGuid(),
                Store = shop,
            };
            db.LoyaltyCards.Add(card);
        }

        card.Format = format;
        card.NumberProtected = protector.Protect(number);
        card.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (created && IsStoreAlreadyTaken(ex))
        {
            // The other person added this shop's card in the same moment; the unique index let one through.
            return Results.Json(new { error = "card_exists", message = $"{shop.DisplayName()} already has a card. Open it to change it." }, statusCode: 409);
        }

        return Results.Json(
            new CardResponse(Key(shop), shop.DisplayName(), CardNumbers.FormatName(format), number, now),
            statusCode: created ? 201 : 200);
    }

    private static bool IsStoreAlreadyTaken(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: StoreIndex };

    private static async Task<IResult> DeleteCard(string store, AppDbContext db, CancellationToken ct) =>
        !Stores.TryParse(store, out var shop) ? UnknownStore()
        : await db.LoyaltyCards.Where(c => c.Store == shop).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound()
        : Results.NoContent();

    private static string Key(Store store) => store.ToString().ToLowerInvariant();

    private static IResult UnknownStore() =>
        Results.Json(new { error = "unknown_store", message = "basket knows Lidl, Aldi, Tesco and Dunnes." }, statusCode: 404);

    private static string NumberHint(CardFormat format) => format switch
    {
        CardFormat.Qr => $"Enter the text the QR code holds, up to {CardNumbers.MaxQrLength} characters.",
        _ => $"Enter the card number: 4 to {CardNumbers.MaxCode128Length} letters or digits, no spaces needed.",
    };
}
