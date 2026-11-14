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

    /// <summary>A printed receipt with invented products, in the structure Lidl prints (spans in a pre); the discount follows its article.</summary>
    internal const string Printed = """
        <html><head><meta http-equiv="content-type" content="text/html; charset=utf-8"/></head><body><pre>
        <span class="header" data-till-country="IE" data-receipt-language="en"><span id="header_line_1"></span> <span id="header_line_2"> Test Store - IE0000000 </span> </span><span class="purchase_list"><span id="purchase_list_line_1" class="currency" data-currency="EUR"> EUR</span>
        <span id="purchase_list_line_2" class="article" data-art-id="1000001" data-unit-price="1.29" data-tax-type="B" data-art-description="Test whole milk 2L">Test whole milk 2L 1.29 B</span>
        <span id="purchase_list_line_3" class="discount" data-promotion-id="1000-IE-TESTPROMO-0001"> Test coupon -0.30</span>
        <span id="purchase_list_line_4"> ----------- </span>
        <span id="purchase_list_line_5" class="article" data-art-id="1000002" data-art-quantity="3" data-unit-price="1.49" data-tax-type="A" data-art-description="Test oat biscuits">Test oat biscuits 4.47 A</span>
        <span id="purchase_list_line_6" class="article" data-art-id="1000002" data-art-quantity="3" data-unit-price="1.49" data-tax-type="A" data-art-description="Test oat biscuits"> 3 x 1.49 </span>
        <span id="purchase_list_line_7" class="article" data-art-id="1000003" data-art-quantity="0.612" data-unit-price="1.69" data-tax-type="A" data-art-description="Test bananas">Test bananas 1.03 A</span>
        <span id="purchase_list_line_8" class="article" data-art-id="1000004" data-unit-price="2,50" data-tax-type="A" data-art-description="Test &amp; sons rye bread">Test &amp; sons rye bread 2,50 A</span>
        <span id="purchase_list_line_9" class="article" data-art-id="1000005" data-unit-price="not a price" data-tax-type="A" data-art-description="Test unreadable">Test unreadable ? A</span>
        </span><span class="purchase_summary"><span id="purchase_summary_1"> ------------</span> <span id="purchase_summary_2">total 9.00</span> </span><span class="return_code"><span id="return_code_1" data-return-code="0"></span></span><span class="footer"></span></pre></body></html>
        """;

    internal static string ReceiptJson(string printed, string date = "2026-11-02T18:30:00+00:00", string total = "\"6,25\"") =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ticketType"] = "Purchase",
            ["id"] = "R-2026-11-02-001",
            ["date"] = date,
            ["totalAmount"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(total),
            ["store"] = new { id = "IE0000", name = "Test Store" },
            ["couponsUsed"] = Array.Empty<object>(),
            ["htmlPrintedReceipt"] = printed,
            ["languageCode"] = "en",
        });

    private static readonly string Receipt = ReceiptJson(Printed);

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
        request.Headers.GetValues("Accept-Language").Should().Equal("en");
        request.Headers.GetValues("App-Version").Should().Equal("16.40.2");
    }

    [Fact]
    public async Task The_app_version_and_the_language_come_from_the_options()
    {
        var handler = new Recorded(_ => Json(HttpStatusCode.OK, """{"tickets":[]}"""));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://tickets.lidl.test/") };
        var client = new LidlPlusTicketsClient(http, Options.Create(new LidlPlusOptions { AppVersion = "17.1.0", Language = "de" }));

        await client.ListAsync(Access, 1, CancellationToken.None);
        await client.GetAsync(Access, "R", CancellationToken.None);

        handler.Seen.Should().HaveCount(2).And.OnlyContain(r =>
            r.Headers.GetValues("App-Version").Single() == "17.1.0" && r.Headers.GetValues("Accept-Language").Single() == "de");
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
    public async Task It_opens_one_receipt_on_the_v3_service_by_its_id_escaped_in_the_path()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, Receipt));

        await client.GetAsync(Access, "R/1 2", CancellationToken.None);

        handler.Seen.Single().RequestUri!.AbsoluteUri.Should().Be("https://tickets.lidl.test/api/v3/IE/tickets/R%2F1%202");
    }

    [Fact]
    public async Task A_receipt_is_read_from_its_printed_lines_with_discounts_quantities_and_unreadable_lines_left_out()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, Receipt));

        var result = await client.GetAsync(Access, "R-2026-11-02-001", CancellationToken.None);

        result.Outcome.Should().Be(LidlTicketsOutcome.Ok);
        var receipt = result.Receipt!;
        receipt.Id.Should().Be("R-2026-11-02-001");
        receipt.Date.Should().Be(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero));
        receipt.Total.Should().Be(6.25m);
        receipt.Lines.Should().Equal(
            new LidlReceiptLine("Test whole milk 2L", 1m, false, 1.29m, 0.99m),
            new LidlReceiptLine("Test oat biscuits", 3m, false, 1.49m, 4.47m),
            new LidlReceiptLine("Test bananas", 0.612m, true, 1.69m, 1.03m),
            new LidlReceiptLine("Test & sons rye bread", 1m, false, 2.50m, 2.50m));
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("12,5", 12.5)]
    [InlineData("0.99", 0.99)]
    public async Task Amounts_in_either_notation_are_read(string text, double expected)
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, ReceiptJson(Printed, total: $"\"{text}\"")));

        var result = await client.GetAsync(Access, "R", CancellationToken.None);

        result.Receipt!.Total.Should().Be((decimal)expected);
    }

    [Fact]
    public async Task A_total_sent_as_a_number_is_read()
    {
        var (client, _) = Client(_ => Json(HttpStatusCode.OK, ReceiptJson(Printed, total: "6.25")));

        (await client.GetAsync(Access, "R", CancellationToken.None)).Receipt!.Total.Should().Be(6.25m);
    }

    [Theory]
    [InlineData("""{"htmlPrintedReceipt":"<span class=\"article\" data-art-id=\"1\" data-unit-price=\"1.00\" data-art-description=\"Test\">x</span>"}""")]
    [InlineData("""{"date":"not a date","htmlPrintedReceipt":"<span class=\"article\" data-art-id=\"1\" data-unit-price=\"1.00\" data-art-description=\"Test\">x</span>"}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z"}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z","htmlPrintedReceipt":null}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z","htmlPrintedReceipt":""}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z","htmlPrintedReceipt":"<pre>no lines in here</pre>"}""")]
    [InlineData("""{"date":"2026-11-02T10:00:00Z","itemsLine":[{"description":"Old shape","quantity":1,"extendedAmount":1.0}]}""")]
    [InlineData("""["a"]""")]
    [InlineData("<html>maintenance</html>")]
    public async Task A_receipt_in_a_shape_basket_cannot_read_is_unreadable_not_unavailable_so_it_is_not_filed_empty(string body)
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
