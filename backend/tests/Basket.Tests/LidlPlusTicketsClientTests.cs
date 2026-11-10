using System.Net;
using System.Text;
using Basket.Api.LidlPlus;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Basket.Tests;

/// <summary>Lidl Plus's receipts service as recorded answers: what basket sends, and how each kind of answer is read. No request leaves the process.</summary>
public sealed class LidlPlusTicketsClientTests
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (LidlPlusTicketsClient Client, Recorded Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Recorded(answer);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://tickets.lidl.test/") };
        return (new LidlPlusTicketsClient(http, Options.Create(new LidlPlusOptions())), handler);
    }

    private const string Receipt = """
        {
          "id": "R-2026-11-02-001",
          "date": "2026-11-02T18:30:00+00:00",
          "totalAmount": "5,16",
          "itemsLine": [
            { "description": "Whole milk 2L", "quantity": "1", "isWeight": false, "currentUnitPrice": "1,29", "originalAmount": "1,29", "extendedAmount": "1,29" },
            { "description": "Bananas", "quantity": "0,612", "isWeight": true, "currentUnitPrice": "1,69", "originalAmount": "1,03", "extendedAmount": "1,03" },
            { "description": "Cola 1.5L", "quantity": 2, "isWeight": false, "currentUnitPrice": 1.2, "originalAmount": 2.4, "extendedAmount": 2.4 },
            { "quantity": "1", "extendedAmount": "9,99" }
          ]
        }
        """;

    [Fact]
    public async Task It_asks_for_one_page_of_the_list_with_the_access_token_in_a_header_and_never_in_the_address()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, """{"tickets":[],"totalCount":0}"""));

        await client.ListAsync(Access, 3, CancellationToken.None);

        var request = handler.Seen.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.ToString().Should().Be("https://tickets.lidl.test/api/v2/IE/tickets?pageNumber=3&onlyFavorite=false").And.NotContain(Access);
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be(Access);
        request.Headers.GetValues("Accept-Language").Should().Equal("en-IE");
    }

    [Fact]
    public async Task It_reads_the_ids_of_the_receipts_on_a_page_in_the_order_given()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, """{"totalCount":2,"size":10,"tickets":[{"id":"B","totalAmount":"1,00"},{"id":"A"},{"noId":true}]}"""));

        var page = await client.ListAsync(Access, 1, CancellationToken.None);

        page.Outcome.Should().Be(LidlTicketsOutcome.Ok);
        page.Receipts.Select(r => r.Id).Should().Equal("B", "A");
    }

    [Fact]
    public async Task It_opens_one_receipt_by_its_id_escaped_in_the_path()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, Receipt));

        await client.GetAsync(Access, "R/1 2", CancellationToken.None);

        handler.Seen.Single().RequestUri!.AbsoluteUri.Should().Be("https://tickets.lidl.test/api/v2/IE/tickets/R%2F1%202");
    }

    [Fact]
    public async Task A_receipt_is_read_with_decimal_commas_numbers_and_the_lines_that_have_no_name_left_out()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, Receipt));

        var result = await client.GetAsync(Access, "R-2026-11-02-001", CancellationToken.None);

        result.Outcome.Should().Be(LidlTicketsOutcome.Ok);
        var receipt = result.Receipt!;
        receipt.Id.Should().Be("R-2026-11-02-001");
        receipt.Date.Should().Be(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero));
        receipt.Total.Should().Be(5.16m);
        receipt.Lines.Select(l => l.Description).Should().Equal("Whole milk 2L", "Bananas", "Cola 1.5L");
        receipt.Lines[0].Should().Be(new LidlReceiptLine("Whole milk 2L", 1m, false, 1.29m, 1.29m));
        receipt.Lines[1].Should().Be(new LidlReceiptLine("Bananas", 0.612m, true, 1.69m, 1.03m));
        receipt.Lines[2].Should().Be(new LidlReceiptLine("Cola 1.5L", 2m, false, 1.2m, 2.4m));
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("12,5", 12.5)]
    [InlineData("0.99", 0.99)]
    public async Task Amounts_in_either_notation_are_read(string text, double expected)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, $$"""{"date":"2026-11-02T10:00:00Z","totalAmount":"{{text}}","itemsLine":[]}"""));

        var result = await client.GetAsync(Access, "R", CancellationToken.None);

        result.Receipt!.Total.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("""{"itemsLine":[]}""")]
    [InlineData("""{"date":"not a date","itemsLine":[]}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z"}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z","itemsLine":"none"}""")]
    [InlineData("""["a"]""")]
    [InlineData("<html>maintenance</html>")]
    public async Task A_receipt_in_a_shape_basket_cannot_read_is_unreadable_not_unavailable(string body)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, body));

        (await client.GetAsync(Access, "R", CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unreadable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Any_answer_but_a_clean_one_makes_the_list_unavailable(HttpStatusCode status)
    {
        var (client, _) = Client(_ => Json(status, "{}"));

        (await client.ListAsync(Access, 1, CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_receipt_Lidl_cannot_serve_just_now_is_unavailable(HttpStatusCode status)
    {
        var (client, _) = Client(_ => Json(status, "{}"));

        (await client.GetAsync(Access, "R", CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_receipt_Lidl_refuses_on_its_own_is_unreadable_so_the_others_are_still_read(HttpStatusCode status)
    {
        var (client, _) = Client(_ => Json(status, "{}"));

        (await client.GetAsync(Access, "R", CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>maintenance</html>")]
    [InlineData("""{"tickets":"none"}""")]
    [InlineData("""{"other":[]}""")]
    [InlineData("""[]""")]
    public async Task A_list_that_is_not_a_list_is_unavailable(string body)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, body));

        (await client.ListAsync(Access, 1, CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unavailable);
    }

    [Fact]
    public async Task A_network_error_is_unavailable_and_never_throws()
    {
        var (client, _) = Client(_ => throw new HttpRequestException($"connection reset while sending {Access}"));

        (await client.ListAsync(Access, 1, CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unavailable);
        (await client.GetAsync(Access, "R", CancellationToken.None)).Outcome.Should().Be(LidlTicketsOutcome.Unavailable);
    }
}
