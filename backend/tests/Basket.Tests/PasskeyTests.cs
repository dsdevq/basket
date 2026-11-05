using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

public sealed class PasskeyTests
{
    private static async Task<JsonElement[]> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement[]>("/api/passkeys"))!;

    /// <summary>Runs the whole add-a-passkey ceremony as a signed-in person, with a fresh software authenticator.</summary>
    private static async Task<(FakeAuthenticator Passkey, HttpResponseMessage Response)> AddAsync(HttpClient client, string? name = null)
    {
        var passkey = new FakeAuthenticator(BasketFactory.Origin);
        var options = await client.PostAsync("/api/passkeys/options", null);
        options.EnsureSuccessStatusCode();
        var credential = passkey.Create(await options.Content.ReadAsStringAsync());
        return (passkey, await client.PostAsJsonAsync("/api/passkeys", new { credentialJson = credential, name }));
    }

    private static async Task<string> SessionUserAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("user").GetProperty("id").GetString()!;

    [Fact]
    public async Task A_new_account_lists_the_one_passkey_it_registered_with()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();

        var passkeys = await ListAsync(owner);

        var only = passkeys.Should().ContainSingle().Subject;
        only.GetProperty("name").GetString().Should().Be(PasskeyEndpoints.DefaultName);
        only.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        only.GetProperty("createdAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_second_passkey_is_added_named_and_signs_in_as_the_same_person()
    {
        await using var factory = new BasketFactory();
        var (owner, firstPasskey) = await factory.OwnerAsync();

        var (phone, added) = await AddAsync(owner, "  New   phone ");

        added.StatusCode.Should().Be(HttpStatusCode.Created);
        (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString().Should().Be("New phone");
        var passkeys = await ListAsync(owner);
        passkeys.Select(p => p.GetProperty("name").GetString()).Should().Equal(PasskeyEndpoints.DefaultName, "New phone");

        var newBrowser = factory.NewBrowser();
        (await factory.SignInAsync(newBrowser, phone)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SessionUserAsync(newBrowser)).Should().Be(await SessionUserAsync(owner));
        var oldBrowser = factory.NewBrowser();
        (await factory.SignInAsync(oldBrowser, firstPasskey)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_blank_name_falls_back_to_the_default_and_a_long_one_is_cut()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();

        await AddAsync(owner, "   ");
        await AddAsync(owner, new string('x', 200));

        var names = (await ListAsync(owner)).Select(p => p.GetProperty("name").GetString()!).ToArray();
        names.Should().HaveCount(3);
        names.Should().OnlyContain(n => n.Length <= PasskeyEndpoints.MaxNameLength);
        names.Count(n => n == PasskeyEndpoints.DefaultName).Should().Be(2);
    }

    [Fact]
    public async Task The_creation_options_exclude_the_passkeys_the_person_already_has()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var existing = (await ListAsync(owner)).Single().GetProperty("id").GetString();

        var options = await owner.PostAsync("/api/passkeys/options", null);

        var body = await options.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("excludeCredentials").EnumerateArray().Select(c => c.GetProperty("id").GetString())
            .Should().Equal(existing);
    }

    [Fact]
    public async Task The_same_authenticator_cannot_be_registered_twice()
    {
        await using var factory = new BasketFactory();
        var (owner, ownerPasskey) = await factory.OwnerAsync();
        var options = await owner.PostAsync("/api/passkeys/options", null);

        var again = await owner.PostAsJsonAsync("/api/passkeys", new { credentialJson = ownerPasskey.Create(await options.Content.ReadAsStringAsync()) });

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ListAsync(owner)).Should().ContainSingle();
    }

    [Fact]
    public async Task Nobody_can_attach_a_passkey_that_already_belongs_to_someone_else()
    {
        await using var factory = new BasketFactory();
        var (owner, ownerPasskey) = await factory.OwnerAsync();
        var (member, _) = await factory.MemberAsync(owner);
        var options = await member.PostAsync("/api/passkeys/options", null);

        var stolen = await member.PostAsJsonAsync("/api/passkeys", new { credentialJson = ownerPasskey.Create(await options.Content.ReadAsStringAsync()) });

        stolen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ListAsync(owner)).Should().ContainSingle();
        (await ListAsync(member)).Should().ContainSingle();
    }

    [Fact]
    public async Task Adding_without_starting_the_ceremony_or_with_nothing_is_refused()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (otherBrowser, _) = await factory.OwnerAsync();

        var options = await owner.PostAsync("/api/passkeys/options", null);
        var credential = new FakeAuthenticator(BasketFactory.Origin).Create(await options.Content.ReadAsStringAsync());

        // The ceremony state lives with the browser that started it.
        (await otherBrowser.PostAsJsonAsync("/api/passkeys", new { credentialJson = credential })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/passkeys", new { credentialJson = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/passkeys", new { credentialJson = "not json" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ListAsync(owner)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_only_passkey_cannot_be_removed()
    {
        await using var factory = new BasketFactory();
        var (owner, passkey) = await factory.OwnerAsync();
        var id = (await ListAsync(owner)).Single().GetProperty("id").GetString();

        var response = await owner.DeleteAsync($"/api/passkeys/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("last_passkey");
        (await ListAsync(owner)).Should().ContainSingle();
        (await factory.SignInAsync(factory.NewBrowser(), passkey)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_passkey_is_removed_once_another_exists_and_it_stops_signing_in()
    {
        await using var factory = new BasketFactory();
        var (owner, first) = await factory.OwnerAsync();
        var (second, _) = await AddAsync(owner, "Phone");
        var firstId = (await ListAsync(owner)).Single(p => p.GetProperty("name").GetString() == PasskeyEndpoints.DefaultName).GetProperty("id").GetString();

        (await owner.DeleteAsync($"/api/passkeys/{firstId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ListAsync(owner)).Select(p => p.GetProperty("name").GetString()).Should().Equal("Phone");
        (await factory.SignInAsync(factory.NewBrowser(), first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.SignInAsync(factory.NewBrowser(), second)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var last = (await ListAsync(owner)).Single().GetProperty("id").GetString();
        (await owner.DeleteAsync($"/api/passkeys/{last}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Two_removals_sent_together_cannot_leave_the_account_with_no_passkey()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await AddAsync(owner, "Phone");
        var ids = (await ListAsync(owner)).Select(p => p.GetProperty("id").GetString()).ToArray();

        var results = await Task.WhenAll(ids.Select(id => owner.DeleteAsync($"/api/passkeys/{id}")));

        results.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.NoContent, HttpStatusCode.Conflict]);
        (await ListAsync(owner)).Should().ContainSingle();
    }

    [Fact]
    public async Task Passkeys_are_private_to_their_owner()
    {
        await using var factory = new BasketFactory();
        var (owner, ownerPasskey) = await factory.OwnerAsync();
        await AddAsync(owner, "Owner phone");
        var (member, _) = await factory.MemberAsync(owner);
        await AddAsync(member, "Member phone");
        var ownerIds = (await ListAsync(owner)).Select(p => p.GetProperty("id").GetString()).ToArray();

        (await ListAsync(member)).Select(p => p.GetProperty("id").GetString()).Should().NotIntersectWith(ownerIds);
        foreach (var id in ownerIds)
        {
            (await member.DeleteAsync($"/api/passkeys/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        (await ListAsync(owner)).Should().HaveCount(2);
        (await factory.SignInAsync(factory.NewBrowser(), ownerPasskey)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public async Task Removing_an_unknown_or_malformed_id_is_a_404(string id)
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await AddAsync(owner);

        (await owner.DeleteAsync($"/api/passkeys/{Uri.EscapeDataString(id)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListAsync(owner)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_person_can_hold_ten_passkeys_and_no_more()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();

        for (var i = 1; i < PasskeyEndpoints.MaxPasskeys; i++)
        {
            (await AddAsync(owner)).Response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await owner.PostAsync("/api/passkeys/options", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ListAsync(owner)).Should().HaveCount(PasskeyEndpoints.MaxPasskeys);
    }

    [Fact]
    public async Task A_member_manages_their_own_passkeys_like_the_owner()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (member, _) = await factory.MemberAsync(owner);

        (await AddAsync(member, "Tablet")).Response.StatusCode.Should().Be(HttpStatusCode.Created);

        (await ListAsync(member)).Should().HaveCount(2);
    }
}
