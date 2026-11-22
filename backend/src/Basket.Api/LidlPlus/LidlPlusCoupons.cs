using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Basket.Api.Prices;
using Microsoft.Extensions.Options;

namespace Basket.Api.LidlPlus;

/// <summary>How Lidl answered a coupons list request. Anything but a list basket can read is <see cref="Unavailable"/>; the token is never blamed here.</summary>
public enum LidlCouponsOutcome
{
    Ok,

    /// <summary>Lidl could not be asked, is down or throttling, turned the access token away, moved the service (the older route answers 404) or answered in a shape basket cannot read as a list.</summary>
    Unavailable,
}

/// <summary>
/// One coupon Lidl lists. <see cref="Discount"/> is Lidl's wording of what it gives ("20% off"); <see cref="Activated"/> is whether it is
/// already switched on for the account.
/// </summary>
public sealed record LidlCouponInfo(string Id, string Title, string? Discount, string? Description, DateOnly? ValidFrom, DateOnly? ValidUntil, bool Activated);

public sealed record LidlCouponList(LidlCouponsOutcome Outcome, IReadOnlyList<LidlCouponInfo> Coupons)
{
    public static LidlCouponList Unavailable { get; } = new(LidlCouponsOutcome.Unavailable, []);
}

/// <summary>How Lidl answered an activation. <see cref="Refused"/> is this one coupon (gone, used up, not for this account); <see cref="Unavailable"/> is Lidl as a whole.</summary>
public enum LidlActivationOutcome
{
    Activated,
    Refused,
    Unavailable,
}

/// <summary>Lidl Plus coupons. The one door to Lidl's coupons service, so tests can stand in for Lidl. The access token is a secret: do not log it.</summary>
public interface ILidlPlusCoupons
{
    /// <summary>Every coupon the account can see, "pick one product" coupons left out.</summary>
    Task<LidlCouponList> ListAsync(string accessToken, CancellationToken ct);

    Task<LidlActivationOutcome> ActivateAsync(string accessToken, string couponId, CancellationToken ct);
}

/// <summary>
/// Reads the coupons list and activates a coupon the way the Lidl Plus app does (v2 <c>promotionslist</c>, then a POST to
/// <c>v1/promotions/{id}/activation</c>, the account's country in a <c>Country</c> header). The service is unofficial and has moved
/// before, so every failure, a 404 included, is reduced to an outcome: nothing here logs a body, a header or an exception message.
/// </summary>
public sealed class LidlPlusCouponsClient(HttpClient http, IOptions<LidlPlusOptions> options) : ILidlPlusCoupons
{
    /// <summary>The coupon types that ask the shopper to choose a product; basket cannot know the choice, so it neither lists nor activates them.</summary>
    private static readonly string[] SelectionTypes = ["special", "assignablepromotion"];

    public async Task<LidlCouponList> ListAsync(string accessToken, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "v2/promotionslist", accessToken, ct);
        if (status != HttpStatusCode.OK || body is null)
        {
            return LidlCouponList.Unavailable;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sections", out var sections)
                || sections.ValueKind != JsonValueKind.Array)
            {
                return LidlCouponList.Unavailable;
            }

            var coupons = new List<LidlCouponInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var readable = 0;
            var unreadable = 0;
            foreach (var section in sections.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object
                    || !section.TryGetProperty("promotions", out var promotions)
                    || promotions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                readable++;
                foreach (var promotion in promotions.EnumerateArray())
                {
                    if (Read(promotion, out var malformed) is { } coupon && seen.Add(coupon.Id))
                    {
                        coupons.Add(coupon);
                    }

                    unreadable += malformed ? 1 : 0;
                }
            }

            if (coupons.Count == 0 && (unreadable > 0 || (readable == 0 && sections.GetArrayLength() > 0)))
            {
                return LidlCouponList.Unavailable;
            }

            return new LidlCouponList(LidlCouponsOutcome.Ok, coupons);
        }
        catch (JsonException)
        {
            return LidlCouponList.Unavailable;
        }
    }

    public async Task<LidlActivationOutcome> ActivateAsync(string accessToken, string couponId, CancellationToken ct)
    {
        var (status, _) = await SendAsync(HttpMethod.Post, $"v1/promotions/{Uri.EscapeDataString(couponId)}/activation", accessToken, ct);
        return status switch
        {
            HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent => LidlActivationOutcome.Activated,
            null or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => LidlActivationOutcome.Unavailable,
            _ => LidlActivationOutcome.Refused,
        };
    }

    /// <summary>One coupon, or null when it is malformed (no id or title, reported in <paramref name="malformed"/>), is a "pick one" coupon, or Lidl marks it as not offered ("apologize").</summary>
    private static LidlCouponInfo? Read(JsonElement promotion, out bool malformed)
    {
        malformed = true;
        if (promotion.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Text(promotion, "id") ?? Text(promotion, "promotionId");
        var discount = promotion.TryGetProperty("discount", out var discountObject) ? discountObject : default;
        var title = Text(promotion, "title") ?? Text(promotion, "offerTitle") ?? Text(discount, "title");
        if (id is null || title is null || id.Length > Data.LidlCoupon.MaxIdLength)
        {
            return null;
        }

        malformed = false;
        if (IsSelection(promotion) || IsApology(promotion))
        {
            return null;
        }

        var validity = promotion.TryGetProperty("validity", out var window) ? window : default;
        var wording = Text(discount, "title") ?? Text(promotion, "discountTitle") ?? Text(promotion, "offerTitle");
        var description = Text(promotion, "description") ?? Text(discount, "description");
        return new LidlCouponInfo(
            id,
            Clip(title, Data.LidlCoupon.MaxTitleLength)!,
            Clip(wording, Data.LidlCoupon.MaxTextLength),
            Clip(description, Data.LidlCoupon.MaxTextLength),
            Day(Text(validity, "start") ?? Text(promotion, "startValidityDate")),
            Day(Text(validity, "end") ?? Text(promotion, "endValidityDate")),
            Flag(promotion, "isActivated") || Flag(promotion, "activated"));
    }

    private static bool IsSelection(JsonElement promotion) =>
        Flag(promotion, "isSpecial")
        || (promotion.TryGetProperty("specialPromotion", out var special) && special.ValueKind == JsonValueKind.Object)
        || (Text(promotion, "type") is { } type && SelectionTypes.Contains(type.ToLowerInvariant()));

    private static bool IsApology(JsonElement promotion) =>
        Flag(promotion, "apologizeStatus")
        || (promotion.TryGetProperty("availability", out var availability) && Flag(availability, "apologizeStatus"));

    private async Task<(HttpStatusCode? Status, string? Body)> SendAsync(HttpMethod method, string path, string accessToken, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept-Language", settings.Language);
        request.Headers.TryAddWithoutValidation("Country", settings.Country);
        request.Headers.TryAddWithoutValidation("App", "com.lidl.eapp");
        request.Headers.TryAddWithoutValidation("Operating-System", "iOS");
        request.Headers.TryAddWithoutValidation("App-Version", settings.AppVersion);
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await http.SendAsync(request, ct);
            return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static DateOnly? Day(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? PriceRecorder.DublinDay(moment) : null;

    private static bool Flag(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
