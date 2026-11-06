using System.Net;
using System.Net.Http.Json;
using Basket.Api.Auth;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

public sealed class PasskeyFailureReportTests : IClassFixture<BasketFactory>
{
    private readonly BasketFactory _factory;

    public PasskeyFailureReportTests(BasketFactory factory) => _factory = factory;

    [Fact]
    public async Task A_signed_out_browser_can_report_a_failed_attempt_and_gets_no_content()
    {
        var response = await _factory.NewBrowser().PostAsJsonAsync("/api/auth/passkey-failure", new
        {
            kind = "blocked",
            errorName = "NotAllowedError",
            stage = "create",
            elapsedMs = 42,
            platformAuthenticator = false,
            elapsedBucket = "instant",
            credentialNull = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_empty_report_is_accepted_too_so_a_broken_client_cannot_hide_the_failure()
    {
        var response = await _factory.NewBrowser().PostAsJsonAsync("/api/auth/passkey-failure", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public void Known_labels_pass_through_unchanged()
    {
        var clean = PasskeyFailureReport.Clean(new("blocked", "NotAllowedError", "create", 42, 200, false, "later", true));

        clean.Should().Be(("blocked", "NotAllowedError", "create", (int?)42, (int?)200, (bool?)false, "later", (bool?)true));
    }

    [Theory]
    [InlineData("a-token-or-a-name-or-anything-else")]
    [InlineData("blocked\r\ninjected line")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_outside_the_fixed_lists_is_logged_as_other(string? text)
    {
        var clean = PasskeyFailureReport.Clean(new(text, text, text, null, null, null, text));

        clean.Should().Be(("other", "other", "other", (int?)null, (int?)null, (bool?)null, (string?)null, (bool?)null));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(999_999_999, PasskeyFailureReport.MaxElapsedMs)]
    public void A_wild_duration_is_clamped(int sent, int logged)
    {
        PasskeyFailureReport.Clean(new(null, null, null, sent, null, null)).ElapsedMs.Should().Be(logged);
    }

    [Fact]
    public void An_http_status_outside_the_real_range_is_dropped()
    {
        PasskeyFailureReport.Clean(new(null, null, null, null, 7, null)).HttpStatus.Should().BeNull();
    }
}
