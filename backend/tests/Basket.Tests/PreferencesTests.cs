using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

public sealed class PreferencesTests : IClassFixture<BasketFactory>
{
    private const string Path = "/api/me/preferences";

    private readonly BasketFactory _factory;

    public PreferencesTests(BasketFactory factory) => _factory = factory;

    private async Task<HttpClient> CleanOwnerAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.UserPreferences.ExecuteDeleteAsync();
            return 0;
        });
        return owner;
    }

    /// <summary>Invites are limited per owner, so a second person is minted only by the tests that need one.</summary>
    private async Task<(HttpClient Owner, HttpClient Her)> CleanPairAsync()
    {
        var owner = await CleanOwnerAsync();
        var (her, _) = await _factory.MemberAsync(owner, "Her");
        return (owner, her);
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, object body) =>
        client.PatchAsJsonAsync(Path, body);

    private static async Task<JsonElement> Appearance(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appearance");
    }

    [Fact]
    public async Task A_person_who_has_saved_nothing_reads_the_defaults()
    {
        var owner = await CleanOwnerAsync();

        var response = await owner.GetAsync(Path);

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var appearance = await Appearance(response);
        appearance.GetProperty("theme").GetString().Should().Be("system");
        appearance.TryGetProperty("seed", out _).Should().BeFalse();
        appearance.TryGetProperty("intensity", out _).Should().BeFalse();
        (await _factory.WithDb(db => db.UserPreferences.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_save_returns_the_whole_object_and_the_next_read_agrees()
    {
        var owner = await CleanOwnerAsync();

        var saved = await Appearance(await Patch(owner, new { appearance = new { theme = "dark", seed = "#7C3AED", intensity = 0.4 } }));
        saved.GetProperty("theme").GetString().Should().Be("dark");
        saved.GetProperty("seed").GetString().Should().Be("#7c3aed");
        saved.GetProperty("intensity").GetDouble().Should().Be(0.4);

        var read = await Appearance(await owner.GetAsync(Path));
        read.GetRawText().Should().Be(saved.GetRawText());
    }

    [Fact]
    public async Task The_appearance_is_written_as_a_whole_so_an_omitted_seed_clears_it()
    {
        var owner = await CleanOwnerAsync();
        await Patch(owner, new { appearance = new { theme = "dark", seed = "#112233", intensity = 0.8 } });

        var cleared = await Appearance(await Patch(owner, new { appearance = new { theme = "light" } }));

        cleared.GetProperty("theme").GetString().Should().Be("light");
        cleared.TryGetProperty("seed", out _).Should().BeFalse();
        cleared.TryGetProperty("intensity", out _).Should().BeFalse();
        (await _factory.WithDb(db => db.UserPreferences.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_patch_without_an_appearance_changes_nothing_and_still_answers_the_full_object()
    {
        var owner = await CleanOwnerAsync();
        await Patch(owner, new { appearance = new { theme = "dark", seed = "#112233" } });

        var untouched = await Appearance(await Patch(owner, new { }));

        untouched.GetProperty("theme").GetString().Should().Be("dark");
        untouched.GetProperty("seed").GetString().Should().Be("#112233");
    }

    [Fact]
    public async Task Each_person_has_their_own_appearance()
    {
        var (owner, her) = await CleanPairAsync();

        await Patch(her, new { appearance = new { theme = "dark", seed = "#aa0000" } });

        (await Appearance(await owner.GetAsync(Path))).GetProperty("theme").GetString().Should().Be("system");
        (await Appearance(await her.GetAsync(Path))).GetProperty("seed").GetString().Should().Be("#aa0000");
    }

    [Theory]
    [InlineData("{\"appearance\":{\"theme\":\"sepia\"}}", "theme_invalid")]
    [InlineData("{\"appearance\":{}}", "theme_invalid")]
    [InlineData("{\"appearance\":{\"theme\":\"dark\",\"seed\":\"7c3aed\"}}", "seed_invalid")]
    [InlineData("{\"appearance\":{\"theme\":\"dark\",\"seed\":\"#7c3aedff\"}}", "seed_invalid")]
    [InlineData("{\"appearance\":{\"theme\":\"dark\",\"intensity\":1.5}}", "intensity_invalid")]
    [InlineData("{\"appearance\":{\"theme\":\"dark\",\"intensity\":-0.1}}", "intensity_invalid")]
    public async Task An_invalid_value_is_refused_and_nothing_is_stored(string body, string error)
    {
        var owner = await CleanOwnerAsync();

        var response = await owner.PatchAsync(Path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be(error);
        (await _factory.WithDb(db => db.UserPreferences.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Two_first_saves_at_once_leave_one_row()
    {
        var owner = await CleanOwnerAsync();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Patch(owner, new { appearance = new { theme = i % 2 == 0 ? "dark" : "light" } })));

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await _factory.WithDb(db => db.UserPreferences.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Removing_a_person_removes_their_preferences()
    {
        var (_, her) = await CleanPairAsync();
        await Patch(her, new { appearance = new { theme = "dark" } });
        var herId = await _factory.WithDb(db => db.UserPreferences.Select(p => p.UserId).SingleAsync());

        await _factory.WithDb(db => db.Users.Where(u => u.Id == herId).ExecuteDeleteAsync());

        (await _factory.WithDb(db => db.UserPreferences.CountAsync())).Should().Be(0);
    }
}
