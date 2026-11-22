using System.Net;
using System.Text;
using Basket.Api.LidlPlus;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Basket.Tests;

/// <summary>Lidl Plus's coupons service as recorded answers: what basket sends, how a list is read, which coupons are skipped, and that every failure is quiet.</summary>
public sealed class LidlPlusCouponsClientTests
{
    private const string Access = "ACCESS-0123456789abcdef";

    private sealed class Recorded(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (LidlPlusCouponsClient Client, Recorded Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Recorded(answer);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://coupons.lidl.test/app/api/") };
        return (new LidlPlusCouponsClient(http, Options.Create(new LidlPlusOptions())), handler);
    }

    /// <summary>The shape of Lidl's sectioned list, with invented coupons: two plain ones, a "pick one" of each kind, and one Lidl apologises for.</summary>
    private const string Listed = """
        {"sections":[
          {"name":"AllStores","promotions":[
            {"id":"C-1","title":"Test chicken breast fillets","discount":{"title":"20% off","description":"Off the fillets"},
             "validity":{"start":"2026-11-16T00:00:00Z","end":"2026-11-22T23:59:00Z"},"isActivated":false,"type":"Standard"},
            {"id":"C-2","title":"Test oat biscuits","offerTitle":"€1 off","validity":{"end":"2026-11-30T00:00:00Z"},"isActivated":true},
            {"id":"C-3","title":"Pick your favourite","type":"AssignablePromotion"},
            {"id":"C-4","title":"Special pick","isSpecial":true},
            {"id":"C-5","title":"Special tag","specialPromotion":{"tag":"x"}},
            {"id":"C-6","title":"Not for you","availability":{"apologizeStatus":true}},
            {"id":"C-1","title":"Test chicken breast fillets again"}
          ]},
          {"name":"Online","promotions":[{"id":12345,"title":"Number id coupon"}]}
        ]}
        """;

    [Fact]
    public async Task The_list_is_asked_the_way_the_app_asks_with_the_country_and_the_token_in_the_header()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, Listed));

        await client.ListAsync(Access, CancellationToken.None);

        var request = handler.Seen.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsolutePath.Should().Be("/app/api/v2/promotionslist");
        request.Headers.Authorization!.ToString().Should().Be($"Bearer {Access}");
        request.Headers.GetValues("Country").Should().Equal("IE");
        request.RequestUri.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task Coupons_are_read_with_their_wording_dates_and_state_and_pick_one_coupons_are_skipped()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, Listed));

        var list = await client.ListAsync(Access, CancellationToken.None);

        list.Outcome.Should().Be(LidlCouponsOutcome.Ok);
        list.Coupons.Select(c => c.Id).Should().Equal("C-1", "C-2", "12345");
        var first = list.Coupons[0];
        first.Title.Should().Be("Test chicken breast fillets");
        first.Discount.Should().Be("20% off");
        first.Description.Should().Be("Off the fillets");
        first.ValidFrom.Should().Be(new DateOnly(2026, 11, 16));
        first.ValidUntil.Should().Be(new DateOnly(2026, 11, 22));
        first.Activated.Should().BeFalse();
        list.Coupons[1].Discount.Should().Be("€1 off");
        list.Coupons[1].Activated.Should().BeTrue();
        list.Coupons[1].ValidFrom.Should().BeNull();
    }

    // Ireland is UTC+1 from the last Sunday of March to the last of October: a UTC end of 23:59 is 00:59 the next Dublin day.
    [Theory]
    [InlineData("2026-07-12T23:59:00Z", 2026, 7, 12)]
    [InlineData("2026-07-12T22:59:00Z", 2026, 7, 12)]
    [InlineData("2026-07-13T00:00:00Z", 2026, 7, 12)]
    [InlineData("2026-12-12T23:59:00Z", 2026, 12, 12)]
    [InlineData("2026-12-13T00:00:00Z", 2026, 12, 12)]
    [InlineData("2026-07-12T12:00:00Z", 2026, 7, 11)]
    public async Task A_coupon_never_counts_on_a_day_that_runs_past_its_stated_end(string end, int year, int month, int day)
    {
        var body = $$"""{"sections":[{"promotions":[{"id":"C-1","title":"Test coupon","validity":{"end":"{{end}}"}}]}]}""";
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, body));

        var list = await client.ListAsync(Access, CancellationToken.None);

        list.Coupons.Single().ValidUntil.Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_list_Lidl_does_not_give_is_unavailable_and_never_throws(HttpStatusCode status)
    {
        var (client, _) = Client(_ => Json(status, "secret body"));

        var list = await client.ListAsync(Access, CancellationToken.None);

        list.Outcome.Should().Be(LidlCouponsOutcome.Unavailable);
        list.Coupons.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"sections\":{}}")]
    public async Task An_answer_that_is_not_a_list_of_sections_is_unavailable(string body)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, body));

        (await client.ListAsync(Access, CancellationToken.None)).Outcome.Should().Be(LidlCouponsOutcome.Unavailable);
    }

    [Theory]
    [InlineData("{\"sections\":[{\"promotions\":[{\"promoId\":\"C-1\",\"name\":\"Chicken\"}]}]}")]
    [InlineData("{\"sections\":[{\"items\":[]}]}")]
    public async Task A_200_whose_coupons_cannot_be_read_is_unavailable_so_the_last_list_is_kept(string body)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, body));

        (await client.ListAsync(Access, CancellationToken.None)).Outcome.Should().Be(LidlCouponsOutcome.Unavailable);
    }

    [Theory]
    [InlineData("{\"sections\":[]}")]
    [InlineData("{\"sections\":[{\"promotions\":[]}]}")]
    [InlineData("{\"sections\":[{\"promotions\":[{\"id\":\"S-1\",\"title\":\"Pick one\",\"isSpecial\":true}]}]}")]
    public async Task A_200_with_no_coupons_to_list_is_an_empty_list(string body)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, body));

        var list = await client.ListAsync(Access, CancellationToken.None);

        list.Outcome.Should().Be(LidlCouponsOutcome.Ok);
        list.Coupons.Should().BeEmpty();
    }

    [Fact]
    public async Task Lidl_being_unreachable_is_unavailable()
    {
        var (client, _) = Client(_ => throw new HttpRequestException("secret detail"));

        (await client.ListAsync(Access, CancellationToken.None)).Outcome.Should().Be(LidlCouponsOutcome.Unavailable);
    }

    [Fact]
    public async Task A_coupon_is_activated_with_a_post_to_its_own_route()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK));

        var outcome = await client.ActivateAsync(Access, "C 1/x", CancellationToken.None);

        outcome.Should().Be(LidlActivationOutcome.Activated);
        var request = handler.Seen.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/app/api/v1/promotions/C%201%2Fx/activation");
        request.Headers.GetValues("Country").Should().Equal("IE");
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, LidlActivationOutcome.Activated)]
    [InlineData(HttpStatusCode.NoContent, LidlActivationOutcome.Activated)]
    [InlineData(HttpStatusCode.BadRequest, LidlActivationOutcome.Refused)]
    [InlineData(HttpStatusCode.NotFound, LidlActivationOutcome.Refused)]
    [InlineData(HttpStatusCode.Unauthorized, LidlActivationOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LidlActivationOutcome.Unavailable)]
    public async Task Each_answer_to_an_activation_is_one_outcome(HttpStatusCode status, LidlActivationOutcome expected)
    {
        var (client, _) = Client(_ => Json(status));

        (await client.ActivateAsync(Access, "C-1", CancellationToken.None)).Should().Be(expected);
    }
}
