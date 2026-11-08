using System.Security.Claims;
using System.Text.Json;
using Basket.Api.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Basket.Api.Prices.Ingest;

public sealed record CreateIngestTokenRequest(string? Label);

public sealed record IngestTokenResponse(Guid Id, string? Label, DateTimeOffset CreatedAt);

/// <summary>The only time the raw token is ever shown: afterwards basket holds just its hash.</summary>
public sealed record CreatedIngestTokenResponse(Guid Id, string? Label, DateTimeOffset CreatedAt, string Token);

public static class IngestEndpoints
{
    public const string Limiter = "ingest";

    /// <summary>A batch of <see cref="PriceIngest.MaxBatch"/> readings is well under 100 KB.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    public static void MapIngestEndpoints(this IEndpointRouteBuilder api)
    {
        // The owner, signed in, issues and revokes the tokens.
        var tokens = api.MapGroup("/ingest-tokens").RequireAuthorization(Policies.Owner);
        tokens.MapGet("/", ListTokens);
        tokens.MapPost("/", CreateToken);
        tokens.MapDelete("/{id:guid}", RevokeToken);

        // The reader posts with a bearer token, not a session. The cookie is never consulted here, which is also why
        // the origin check lets a bearer request through: nothing ambient can be forged into it.
        api.MapPost("/ingest/prices", PostPrices)
            .AllowAnonymous()
            .RequireRateLimiting(Limiter)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
    }

    private static async Task<IResult> ListTokens(IngestTokenService tokens, CancellationToken ct) =>
        Results.Ok((await tokens.ListAsync(ct)).Select(t => new IngestTokenResponse(t.Id, t.Label, t.CreatedAt)));

    private static async Task<IResult> CreateToken(
        CreateIngestTokenRequest request,
        ClaimsPrincipal principal,
        IngestTokenService tokens,
        CancellationToken ct)
    {
        var createdBy = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var (token, raw) = await tokens.CreateAsync(createdBy, request.Label, ct);
        return Results.Created($"/api/ingest-tokens/{token.Id}", new CreatedIngestTokenResponse(token.Id, token.Label, token.CreatedAt, raw));
    }

    private static async Task<IResult> RevokeToken(Guid id, IngestTokenService tokens, CancellationToken ct) =>
        await tokens.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> PostPrices(
        HttpContext http,
        IngestTokenService tokens,
        FleetAgentAuthenticator fleet,
        PriceIngest ingest,
        ILogger<PriceIngest> log,
        CancellationToken ct)
    {
        // Who is asking comes first, so a caller without a token learns nothing about the request's shape. The
        // owner-minted bsk_ token is judged exactly as before; only when it is not one does a fleet token get a look.
        if (!await tokens.AuthenticateAsync(IngestTokens.FromHeader(http.Request.Headers.Authorization), ct))
        {
            var caller = await fleet.AuthenticateAsync(http);
            if (caller is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "unauthorized", message = "A valid price token is required." }, statusCode: 401);
            }

            if (!caller.Has(FleetScopes.PriceIngest))
            {
                return Results.Json(new { error = "forbidden", message = "That token may not send prices." }, statusCode: 403);
            }

            log.LogInformation("Fleet agent {ClientId} authorised for {Scope}", caller.ClientId, FleetScopes.PriceIngest);
        }

        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return Results.Json(new { error = "too_large", message = "That batch is too big." }, statusCode: 413);
        }

        if (!http.Request.HasJsonContentType())
        {
            return Results.Json(new { error = "json_required", message = "Send application/json." }, statusCode: 415);
        }

        IngestRequest? body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<IngestRequest>(ct);
        }
        catch (JsonException)
        {
            return BadBatch("The body is not the JSON basket expects.");
        }

        if (body?.Prices is not { Count: > 0 } prices || prices.Count > PriceIngest.MaxBatch || prices.Any(p => p is null))
        {
            return BadBatch($"Send between 1 and {PriceIngest.MaxBatch} readings in \"prices\".");
        }

        IngestResponse result;
        try
        {
            result = await ingest.IngestAsync(prices, ct);
        }
        catch (DbUpdateException ex)
        {
            // Two batches for the same listing and day at the same moment; the loser can simply send again.
            log.LogWarning(ex, "A price batch collided with another write");
            return Results.Json(new { error = "conflict", message = "Another batch was being saved. Send it again." }, statusCode: 409);
        }

        log.LogInformation("Price ingest: {Accepted} accepted, {Rejected} rejected", result.Accepted, result.Rejected);
        return Results.Json(result, statusCode: result.Accepted == 0 ? 422 : 200);
    }

    private static IResult BadBatch(string message) =>
        Results.Json(new { error = "batch_invalid", message }, statusCode: 400);
}
