using Basket.Api.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.AspNetCore;
using Serilog.Extensions.Hosting;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace Basket.Tests;

public sealed class RequestLoggingTests
{
    private static Dictionary<string, LogEventPropertyValue> Enrich(Action<IHeaderDictionary> headers)
    {
        var options = new RequestLoggingOptions();
        RequestLogging.Configure(options);
        var context = new DefaultHttpContext();
        headers(context.Request.Headers);
        var diagnostics = new DiagnosticContext(new LoggerConfiguration().CreateLogger());
        using var collector = diagnostics.BeginCollection();
        options.EnrichDiagnosticContext!(diagnostics, context);
        collector.TryComplete(out var properties, out _).Should().BeTrue();
        return properties!.ToDictionary(p => p.Name, p => p.Value);
    }

    private static string Render(Dictionary<string, LogEventPropertyValue> extra, string path)
    {
        var properties = new Dictionary<string, LogEventPropertyValue>(extra)
        {
            ["RequestMethod"] = new ScalarValue("GET"),
            ["RequestPath"] = new ScalarValue(path),
            ["StatusCode"] = new ScalarValue(200),
            ["Elapsed"] = new ScalarValue(1.5),
        };
        var template = new MessageTemplateParser().Parse(RequestLogging.MessageTemplate);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null, template,
            properties.Select(p => new LogEventProperty(p.Key, p.Value)));
        using var writer = new StringWriter();
        new MessageTemplateTextFormatter("{Message:lj}").Format(logEvent, writer);
        return writer.ToString();
    }

    [Fact]
    public void The_request_line_names_the_browser()
    {
        var properties = Enrich(h =>
        {
            h.UserAgent = "Mozilla/5.0 (Odd Phone) OddBrowser/9.1\r\ninjected";
            h["Sec-CH-UA"] = "\"Odd\";v=\"9\"";
            h.AcceptLanguage = "ga-IE,en;q=0.5";
        });

        var line = Render(properties, "/api/session");

        line.Should().StartWith("HTTP GET /api/session responded 200 in ")
            .And.Contain("OddBrowser/9.1  injected")
            .And.Contain("\"Odd\";v=\"9\"")
            .And.Contain("ga-IE,en;q=0.5")
            .And.NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void Missing_headers_are_logged_as_a_dash()
    {
        var line = Render(Enrich(_ => { }), "/");

        line.Should().EndWith("ua=- ch-ua=- lang=-");
    }

    [Fact]
    public void The_template_has_no_slot_for_a_query_string_or_a_fragment()
    {
        RequestLogging.MessageTemplate.Should().NotContainAny("RequestQuery", "QueryString", "Fragment", "Token");
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("", "-")]
    [InlineData("   ", "-")]
    [InlineData("  Agent/1.0  ", "Agent/1.0")]
    [InlineData("a\r\nb\tc", "a  b c")]
    public void Clean_trims_blanks_and_control_characters(string? input, string expected) =>
        RequestLogging.Clean(input).Should().Be(expected);

    [Fact]
    public void Clean_caps_the_length() =>
        RequestLogging.Clean(new string('x', 1000)).Should().HaveLength(256);
}
