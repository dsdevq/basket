using Basket.Api.Auth;

namespace Basket.Api.LidlPlus;

public sealed record SaveLidlPlusTokenRequest(string? RefreshToken);

/// <summary>Where the connection stands. It never carries the token, whole or in part.</summary>
public sealed record LidlPlusStatusResponse(string State, DateTimeOffset? ConnectedAt, DateTimeOffset? VerifiedAt);

/// <summary>
/// The household's Lidl Plus connection, owner only: paste the refresh token once, see whether it works, check it again, remove it.
/// The token travels in a request body, never in a path or query, is checked with Lidl before it is kept, is stored encrypted
/// and is never returned or logged. Answers are marked <c>no-store</c>.
/// </summary>
public static class LidlPlusEndpoints
{
    public static void MapLidlPlusEndpoints(this IEndpointRouteBuilder api)
    {
        var connection = api.MapGroup("/lidl-plus/connection").RequireAuthorization(Policies.Owner).AddEndpointFilter(NoStore);
        connection.MapGet("/", GetStatus);
        connection.MapPut("/", Connect);
        connection.MapPost("/check", Check);
        connection.MapDelete("/", Disconnect);
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static async Task<IResult> GetStatus(LidlPlusConnectionService service, CancellationToken ct) =>
        Results.Ok(Respond(await service.StatusAsync(ct)));

    private static async Task<IResult> Connect(SaveLidlPlusTokenRequest request, LidlPlusConnectionService service, CancellationToken ct)
    {
        // The message states the rule and never repeats what was pasted: a refused token is still a password.
        var token = LidlPlusTokens.Normalize(request.RefreshToken);
        if (token is null)
        {
            return Results.Json(
                new { error = "token_invalid", message = "That does not look like a Lidl Plus refresh token. Paste the whole token, with no spaces." },
                statusCode: 400);
        }

        var (outcome, created) = await service.ConnectAsync(token, ct);
        return outcome switch
        {
            ConnectOutcome.Connected => Results.Json(Respond(await service.StatusAsync(ct)), statusCode: created ? 201 : 200),
            ConnectOutcome.Rejected => Results.Json(
                new { error = "token_rejected", message = "Lidl did not accept that token. It may have expired or been used already; get a fresh one." },
                statusCode: 422),
            _ => Results.Json(
                new { error = "lidl_unavailable", message = "Could not reach Lidl just now. Nothing was saved; try again in a minute." },
                statusCode: 502),
        };
    }

    /// <summary>Asks Lidl again with the stored token, so the status shows whether it still works.</summary>
    private static async Task<IResult> Check(LidlPlusConnectionService service, CancellationToken ct)
    {
        var access = await service.AccessAsync(ct);
        if (access.State == LidlPlusState.NotConnected)
        {
            return Results.Json(new { error = "not_connected", message = "Lidl Plus is not connected." }, statusCode: 404);
        }

        if (access.State == LidlPlusState.Connected && access.AccessToken is null)
        {
            return Results.Json(
                new { error = "lidl_unavailable", message = "Could not reach Lidl just now. The token was left as it is; try again in a minute." },
                statusCode: 502);
        }

        return Results.Ok(Respond(await service.StatusAsync(ct)));
    }

    private static async Task<IResult> Disconnect(LidlPlusConnectionService service, CancellationToken ct) =>
        await service.DisconnectAsync(ct) ? Results.NoContent() : Results.NotFound();

    private static LidlPlusStatusResponse Respond(LidlPlusStatus status) =>
        new(StateName(status.State), status.ConnectedAt, status.VerifiedAt);

    private static string StateName(LidlPlusState state) => state switch
    {
        LidlPlusState.NotConnected => "not_connected",
        LidlPlusState.Connected => "connected",
        LidlPlusState.NeedsNewToken => "needs_new_token",
        LidlPlusState.Unreadable => "unreadable",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
