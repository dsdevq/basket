using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Prices;

namespace Basket.Api.LidlPlus;

/// <summary>One coupon as the owner sees it. <see cref="Counts"/> says whether it changes a price today (activated and in date).</summary>
public sealed record LidlCouponResponse(string Id, string Title, string? Discount, string? Description, DateOnly? ValidFrom, DateOnly? ValidUntil, bool Activated, bool Counts);

/// <param name="State">The connection: <c>connected</c>, <c>not_connected</c> or <c>needs_new_token</c>.</param>
/// <param name="Fresh">True when the list is what Lidl said just now; false when Lidl could not be asked and this is the last list basket kept.</param>
public sealed record LidlCouponsResponse(string State, bool Fresh, IReadOnlyList<LidlCouponResponse> Coupons, int Activated = 0, int Refused = 0);

/// <summary>
/// The household's Lidl Plus coupons, owner only: list them, switch one on, switch every one on. Lidl's coupon service is unofficial
/// and may not answer, so a list that cannot be read is answered with the last one basket kept (<c>fresh: false</c>), not with an
/// error; only an action the owner asked for and Lidl could not do is a 502. Answers are marked <c>no-store</c>.
/// </summary>
public static class LidlCouponEndpoints
{
    public static void MapLidlCouponEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/lidl-plus/coupons").RequireAuthorization(Policies.Owner).AddEndpointFilter(NoStore);
        group.MapGet("/", List);
        group.MapPost("/activate", ActivateAll);
        group.MapPost("/{id}/activate", ActivateOne);
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static async Task<IResult> List(LidlCouponService service, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await service.SyncAsync(ct);
        return Results.Ok(await Respond(service, clock, outcome, outcome == CouponSyncOutcome.Done, 0, 0, ct));
    }

    private static Task<IResult> ActivateAll(LidlCouponService service, TimeProvider clock, CancellationToken ct) =>
        Activate(service, clock, null, ct);

    private static Task<IResult> ActivateOne(string id, LidlCouponService service, TimeProvider clock, CancellationToken ct) =>
        Activate(service, clock, id, ct);

    private static async Task<IResult> Activate(LidlCouponService service, TimeProvider clock, string? id, CancellationToken ct)
    {
        var result = await service.ActivateAsync(id, ct);
        if (result.Unknown)
        {
            return Results.Json(new { error = "unknown_coupon", message = "Lidl does not list that coupon any more." }, statusCode: 404);
        }

        return result.Outcome switch
        {
            CouponSyncOutcome.NotConnected => Results.Json(new { error = "not_connected", message = "Lidl Plus is not connected." }, statusCode: 404),
            CouponSyncOutcome.NeedsNewToken => Results.Json(
                new { error = "needs_new_token", message = "Lidl no longer accepts the saved token. Paste a new one." }, statusCode: 409),
            CouponSyncOutcome.Unavailable when result.Activated == 0 => Results.Json(
                new { error = "lidl_unavailable", message = "Could not reach Lidl's coupons just now. Nothing was changed; try again in a minute." },
                statusCode: 502),
            _ => Results.Ok(await Respond(service, clock, CouponSyncOutcome.Done, true, result.Activated, result.Refused, ct)),
        };
    }

    private static async Task<LidlCouponsResponse> Respond(
        LidlCouponService service, TimeProvider clock, CouponSyncOutcome outcome, bool fresh, int activated, int refused, CancellationToken ct)
    {
        var today = PriceRecorder.DublinDay(clock.GetUtcNow());
        var coupons = outcome == CouponSyncOutcome.NotConnected ? [] : await service.StoredAsync(ct);
        return new LidlCouponsResponse(
            outcome switch
            {
                CouponSyncOutcome.NotConnected => "not_connected",
                CouponSyncOutcome.NeedsNewToken => "needs_new_token",
                _ => "connected",
            },
            fresh,
            coupons.Select(c => new LidlCouponResponse(
                c.Id, c.Title, c.Discount, c.Description, c.ValidFrom, c.ValidUntil, c.Activated, c.Activated && LidlCouponService.InDate(c.ValidFrom, c.ValidUntil, today))).ToList(),
            activated,
            refused);
    }
}
