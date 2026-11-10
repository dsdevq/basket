using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Cards;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

public sealed class CardTests : IClassFixture<BasketFactory>
{
    private const string TescoNumber = "634004 0123 4567 8901 23";
    private const string TescoCompact = "634004012345678901" + "23";

    private readonly BasketFactory _factory;

    public CardTests(BasketFactory factory) => _factory = factory;

    private async Task<HttpClient> CleanOwnerAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.LoyaltyCards.ExecuteDeleteAsync();
            return 0;
        });
        return owner;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_card_is_added_once_and_both_people_see_it()
    {
        var owner = await CleanOwnerAsync();
        var (her, _) = await _factory.MemberAsync(owner, "Her");

        var added = await her.PutAsJsonAsync("/api/cards/tesco", new { number = TescoNumber });
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var card = await Body(added);
        card.GetProperty("store").GetString().Should().Be("tesco");
        card.GetProperty("format").GetString().Should().Be("code128");
        card.GetProperty("number").GetString().Should().Be(TescoCompact);

        var again = await owner.PutAsJsonAsync("/api/cards/tesco", new { number = "111122223333444455" });
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.WithDb(db => db.LoyaltyCards.CountAsync())).Should().Be(1);
        var seen = await Body(await her.GetAsync("/api/cards/tesco"));
        seen.GetProperty("number").GetString().Should().Be("111122223333444455");
    }

    [Fact]
    public async Task Each_shop_has_the_code_its_card_normally_shows_and_the_code_can_be_changed()
    {
        var owner = await CleanOwnerAsync();

        var lidl = await Body(await owner.PutAsJsonAsync("/api/cards/lidl", new { number = "LIDLPLUS-0001" }));
        lidl.GetProperty("format").GetString().Should().Be("qr");
        var aldi = await Body(await owner.PutAsJsonAsync("/api/cards/aldi", new { number = "9876543210" }));
        aldi.GetProperty("format").GetString().Should().Be("code128");
        var dunnes = await Body(await owner.PutAsJsonAsync("/api/cards/dunnes", new { number = "4006381333931", format = "qr" }));
        dunnes.GetProperty("format").GetString().Should().Be("qr");

        var list = (await Body(await owner.GetAsync("/api/cards"))).GetProperty("cards").EnumerateArray().ToArray();
        list.Select(c => c.GetProperty("store").GetString()).Should().Equal("lidl", "aldi", "dunnes");
    }

    [Fact]
    public async Task The_list_hides_the_number_and_only_the_single_card_returns_it()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/tesco", new { number = TescoNumber });

        var response = await owner.GetAsync("/api/cards");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(TescoCompact).And.NotContain("634004");
        var card = JsonDocument.Parse(raw).RootElement.GetProperty("cards")[0];
        card.GetProperty("masked").GetString().Should().Be("•••• 8901".Replace("8901", TescoCompact[^4..]));
        card.GetProperty("readable").GetBoolean().Should().BeTrue();
        card.TryGetProperty("number", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_number_is_encrypted_in_the_database()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/dunnes", new { number = TescoNumber });

        var stored = await _factory.WithDb(db => db.LoyaltyCards.AsNoTracking().Select(c => c.NumberProtected).SingleAsync());
        stored.Should().NotContain(TescoCompact).And.NotContain("634004");
        var protector = (CardNumberProtector)_factory.Services.GetService(typeof(CardNumberProtector))!;
        protector.Unprotect(stored).Should().Be(TescoCompact);
    }

    [Fact]
    public async Task The_number_is_never_logged()
    {
        var owner = await CleanOwnerAsync();
        var secret = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (b % 10).ToString()));

        await owner.PutAsJsonAsync("/api/cards/tesco", new { number = secret });
        await owner.GetAsync("/api/cards/tesco");
        await owner.GetAsync("/api/cards");
        await owner.PutAsJsonAsync("/api/cards/tesco", new { number = secret + "é", format = "code128" });
        await owner.PutAsJsonAsync("/api/cards/tesco", new { number = secret, format = "nonsense" });
        var protectedValue = await _factory.WithDb(db => db.LoyaltyCards.AsNoTracking().Select(c => c.NumberProtected).SingleAsync());

        // The run's own requests were logged (the capture works), and the number is in none of them.
        _factory.Logs.Lines.Should().Contain(line => line.Contains("/api/cards/tesco", StringComparison.Ordinal));
        _factory.Logs.Lines.Should().NotContain(line => line.Contains(secret, StringComparison.Ordinal) || line.Contains(protectedValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Responses_are_never_cached()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/tesco", new { number = TescoNumber });

        foreach (var response in new[] { await owner.GetAsync("/api/cards"), await owner.GetAsync("/api/cards/tesco") })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("tesco", "", null)]
    [InlineData("tesco", "   ", null)]
    [InlineData("tesco", "abc", null)]
    [InlineData("tesco", "héllo123", "code128")]
    [InlineData("tesco", "4006381333931", "ean13")]
    [InlineData("tesco", "4006381333931", "pdf417")]
    public async Task A_number_that_cannot_be_drawn_is_refused_without_repeating_it(string store, string number, string? format)
    {
        var owner = await CleanOwnerAsync();

        var response = await owner.PutAsJsonAsync($"/api/cards/{store}", new { number, format });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        if (number.Trim().Length > 0)
        {
            text.Should().NotContain(number.Trim());
        }

        (await _factory.WithDb(db => db.LoyaltyCards.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_missing_number_is_refused()
    {
        var owner = await CleanOwnerAsync();

        (await owner.PutAsJsonAsync("/api/cards/tesco", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_shop_basket_does_not_know_is_a_404()
    {
        var owner = await CleanOwnerAsync();

        (await owner.PutAsJsonAsync("/api/cards/spar", new { number = "123456" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync("/api/cards/spar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.DeleteAsync("/api/cards/spar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_card_can_be_removed()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/aldi", new { number = "123456789" });

        (await owner.DeleteAsync("/api/cards/aldi")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetAsync("/api/cards/aldi")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.DeleteAsync("/api/cards/aldi")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_database_holds_one_card_per_shop()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/aldi", new { number = "123456789" });
        var userId = await _factory.WithDb(db => db.Users.Select(u => u.Id).FirstAsync());

        var duplicate = async () => await _factory.WithDb(async db =>
        {
            db.LoyaltyCards.Add(new LoyaltyCard
            {
                Id = Guid.NewGuid(), Store = Store.Aldi, Format = CardFormat.Code128, NumberProtected = "x",
                AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, AddedByUserId = userId,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Two_people_adding_the_same_shop_at_once_leave_one_card_and_the_loser_is_told_it_exists()
    {
        var owner = await CleanOwnerAsync();
        var userId = await _factory.WithDb(db => db.Users.Select(u => u.Id).FirstAsync());
        _factory.BeforeCardInsert = () => _factory.WithDb(async db =>
        {
            db.LoyaltyCards.Add(new LoyaltyCard
            {
                Id = Guid.NewGuid(), Store = Store.Tesco, Format = CardFormat.Code128, NumberProtected = "the-other-persons",
                AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, AddedByUserId = userId,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        try
        {
            var lost = await owner.PutAsJsonAsync("/api/cards/tesco", new { number = TescoNumber });

            lost.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Body(lost)).GetProperty("error").GetString().Should().Be("card_exists");
            (await _factory.WithDb(db => db.LoyaltyCards.CountAsync())).Should().Be(1);
        }
        finally
        {
            _factory.BeforeCardInsert = null;
        }
    }

    [Fact]
    public async Task A_database_failure_other_than_a_duplicate_shop_is_not_reported_as_an_existing_card()
    {
        var owner = await CleanOwnerAsync();
        var (her, _) = await _factory.MemberAsync(owner, "Leaves mid-save");
        // The person saving is deleted between sign-in and the insert: a foreign-key failure, not a duplicate shop.
        _factory.BeforeCardInsert = () => _factory.WithDb(db => db.Users.Where(u => u.DisplayName == "Leaves mid-save").ExecuteDeleteAsync());

        try
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await her.PutAsJsonAsync("/api/cards/aldi", new { number = "123456789" });
            }
            catch (Exception)
            {
                // The host lets an unexpected failure surface; that is the outcome wanted, anything but a 409.
            }

            response?.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await _factory.WithDb(db => db.LoyaltyCards.CountAsync())).Should().Be(0);
        }
        finally
        {
            _factory.BeforeCardInsert = null;
        }
    }

    [Fact]
    public async Task A_number_that_cannot_be_decrypted_is_reported_not_guessed()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/aldi", new { number = "123456789" });
        await _factory.WithDb(async db =>
        {
            await db.LoyaltyCards.ExecuteUpdateAsync(s => s.SetProperty(c => c.NumberProtected, "not-a-protected-value"));
            return 0;
        });

        var list = (await Body(await owner.GetAsync("/api/cards"))).GetProperty("cards")[0];
        list.GetProperty("readable").GetBoolean().Should().BeFalse();
        var card = await Body(await owner.GetAsync("/api/cards/aldi"));
        card.GetProperty("number").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_fleet_identity_cannot_reach_the_cards()
    {
        var owner = await CleanOwnerAsync();
        await owner.PutAsJsonAsync("/api/cards/aldi", new { number = "123456789" });
        var fleet = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        fleet.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Fleet.Mint("basket:catalog-read basket:catalog-add basket:price-ingest"));

        (await fleet.GetAsync("/api/cards")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fleet.GetAsync("/api/cards/aldi")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(CardFormat.Code128, "  6340 0401-2345 ", "634004012345")]
    [InlineData(CardFormat.Code128, "ABC123", "ABC123")]
    [InlineData(CardFormat.Code128, "abc", null)]
    [InlineData(CardFormat.Code128, "héllo123", null)]
    [InlineData(CardFormat.Qr, "  some text with spaces  ", "some text with spaces")]
    [InlineData(CardFormat.Qr, "line\nbreak", null)]
    public void Numbers_are_normalized_for_the_code_they_will_be_drawn_as(CardFormat format, string raw, string? expected) =>
        CardNumbers.Normalize(format, raw).Should().Be(expected);

    [Theory]
    [InlineData("1234", "••••")]
    [InlineData("1234567890", "•••• 7890")]
    public void Masking_keeps_only_the_last_four(string number, string expected) => CardNumbers.Mask(number).Should().Be(expected);
}
