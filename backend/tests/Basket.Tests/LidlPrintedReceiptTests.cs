using Basket.Api.LidlPlus;
using FluentAssertions;

namespace Basket.Tests;

/// <summary>The printed receipt of Lidl's receipts service, read line by line. Every receipt here is invented; none is a real one.</summary>
public sealed class LidlPrintedReceiptTests
{
    private static string Article(string id, string name, string price, string? quantity = null, string? text = null)
    {
        var quantityAttribute = quantity is null ? string.Empty : " data-art-quantity=\"" + quantity + "\"";
        return "<span id=\"l\" class=\"article\" data-art-id=\"" + id + "\"" + quantityAttribute + " data-unit-price=\"" + price
            + "\" data-tax-type=\"A\" data-art-description=\"" + name + "\">" + (text ?? name + " " + price + " A") + "</span>";
    }

    private static string Discount(string text, string id = "1000-IE-TEST-0001") =>
        $"""<span id="l" class="discount" data-promotion-id="{id}"> {text}</span>""";

    private static string Receipt(params string[] spans) =>
        $"""<html><body><pre><span class="purchase_list"><span id="c" class="currency" data-currency="EUR"> EUR</span> {string.Join(" ", spans)} </span><span class="purchase_summary"><span id="s">total 1.00</span></span></pre></body></html>""";

    [Fact]
    public void An_article_is_a_line_of_one_at_its_shelf_price()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("1", "Test tea bags", "1.99")));

        lines.Should().Equal(new LidlReceiptLine("Test tea bags", 1m, false, 1.99m, 1.99m));
    }

    [Fact]
    public void A_line_bought_three_times_is_one_line_and_its_x_line_is_not_counted_again()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(
            Article("7", "Test yoghurt", "1.49", quantity: "3", text: "Test yoghurt 4.47 A"),
            Article("7", "Test yoghurt", "1.49", quantity: "3", text: " 3 x 1.49 ")));

        lines.Should().Equal(new LidlReceiptLine("Test yoghurt", 3m, false, 1.49m, 4.47m));
    }

    [Fact]
    public void A_line_with_a_quantity_only_in_its_x_line_takes_the_quantity_from_it()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("7", "Test yoghurt", "1.49"), Article("7", "Test yoghurt", "1.49", text: " 2 x 1.49 ")));

        lines.Should().Equal(new LidlReceiptLine("Test yoghurt", 2m, false, 1.49m, 2.98m));
    }

    [Fact]
    public void The_same_article_scanned_twice_is_two_lines()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("7", "Test yoghurt", "1.49"), Article("7", "Test yoghurt", "1.49")));

        lines.Should().HaveCount(2);
    }

    [Fact]
    public void An_article_whose_name_starts_with_a_count_is_not_mistaken_for_an_x_line()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("7", "Test pack", "1.49"), Article("7", "2 x 1.5L Test cola", "1.99")));

        lines.Select(l => l.Description).Should().Equal("Test pack", "2 x 1.5L Test cola");
    }

    [Fact]
    public void A_weighed_line_has_its_weight_for_quantity_and_the_price_of_a_kilo()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("3", "Test apples", "2.40", quantity: "0.512", text: "Test apples 1.23 A")));

        lines.Should().Equal(new LidlReceiptLine("Test apples", 0.512m, true, 2.40m, 1.23m));
    }

    [Fact]
    public void A_weight_with_a_decimal_comma_is_read()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("3", "Test apples", "2,40", quantity: "0,512")));

        lines.Should().Equal(new LidlReceiptLine("Test apples", 0.512m, true, 2.40m, 1.23m));
    }

    [Theory]
    [InlineData("Test coupon -0.30", 0.69)]
    [InlineData("Test coupon -0,30", 0.69)]
    [InlineData("Test coupon 0.30-", 0.69)]
    public void A_discount_comes_off_the_article_before_it(string discount, double expected)
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("1", "Test milk", "0.99"), Discount(discount), Article("2", "Test tea", "1.99")));

        lines.Select(l => l.Amount).Should().Equal((decimal)expected, 1.99m);
    }

    [Fact]
    public void Two_discounts_both_come_off_and_a_discount_after_an_x_line_comes_off_the_whole_line()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(
            Article("7", "Test yoghurt", "1.49", quantity: "3", text: "Test yoghurt 4.47 A"),
            Article("7", "Test yoghurt", "1.49", quantity: "3", text: " 3 x 1.49 "),
            Discount("Test deal -0.47"),
            Discount("Test coupon -1.00")));

        lines.Should().Equal(new LidlReceiptLine("Test yoghurt", 3m, false, 1.49m, 3.00m));
    }

    [Fact]
    public void A_discount_with_no_article_before_it_or_no_amount_is_left_out()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(
            Discount("Test coupon -1.00"),
            Article("1", "Test milk", "0.99"),
            Discount("Test coupon, percent off"),
            Discount("Test coupon -10%")));

        lines.Should().Equal(new LidlReceiptLine("Test milk", 1m, false, 0.99m, 0.99m));
    }

    [Fact]
    public void A_discount_after_a_line_that_could_not_be_read_does_not_land_on_the_line_before()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(
            Article("1", "Test milk", "0.99"),
            Article("2", "Test broken", "n/a"),
            Discount("Test coupon -0.50")));

        lines.Should().Equal(new LidlReceiptLine("Test milk", 1m, false, 0.99m, 0.99m));
    }

    [Fact]
    public void A_returned_deposit_is_a_line_with_a_negative_amount_for_the_importer_to_leave_out()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(Article("9", "Test deposit returned", "-0.15")));

        lines.Should().Equal(new LidlReceiptLine("Test deposit returned", 1m, false, -0.15m, -0.15m));
    }

    [Fact]
    public void Names_are_decoded_and_attributes_may_be_single_quoted_or_in_any_order_and_case()
    {
        const string html = """<PRE><SPAN data-art-description='Test &quot;fresh&quot; &amp; tasty' class='article  featured' data-unit-price='3.00' data-art-id='5'>x</SPAN></PRE>""";

        LidlPrintedReceipt.ReadLines(html).Should().Equal(new LidlReceiptLine("Test \"fresh\" & tasty", 1m, false, 3.00m, 3.00m));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not html at all")]
    [InlineData("<pre><span class=\"purchase_summary\">total 1.00</span></pre>")]
    [InlineData("<span class=\"article\" data-art-id=\"1\" data-unit-price=\"1.00\" data-art-description=\"\">x</span>")]
    [InlineData("<span class=\"article\" data-art-id=\"1\" data-art-description=\"Test\">no price</span>")]
    [InlineData("<span class=\"article\" data-unit-price=\"1.00\" data-art-description=\"Test\" data-art-quantity=\"0\">none bought</span>")]
    [InlineData("<span class=\"article")]
    public void Nothing_readable_is_no_lines_and_never_an_error(string html)
    {
        LidlPrintedReceipt.ReadLines(html).Should().BeEmpty();
    }

    [Fact]
    public void A_line_that_cannot_be_read_does_not_cost_the_lines_around_it()
    {
        var lines = LidlPrintedReceipt.ReadLines(Receipt(
            Article("1", "Test milk", "0.99"),
            """<span class="article" data-art-id="2">broken</span>""",
            Article("3", "Test tea", "1.99")));

        lines.Select(l => l.Description).Should().Equal("Test milk", "Test tea");
    }
}
