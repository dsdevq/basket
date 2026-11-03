using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

public sealed class InviteLifecycleTests
{
    private static async Task<HttpResponseMessage> CheckAsync(HttpClient client, string token) =>
        await client.PostAsJsonAsync("/api/auth/invite", new { token });

    [Fact]
    public async Task The_first_run_bootstrap_opens_an_owner_link_and_stores_only_its_hash()
    {
        await using var factory = new BasketFactory();
        var client = factory.NewBrowser();

        var check = await CheckAsync(client, BasketFactory.BootstrapToken);

        check.StatusCode.Should().Be(HttpStatusCode.OK);
        (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString().Should().Be("owner");
        var rows = await factory.WithDb(db => db.Invites.ToListAsync());
        rows.Should().ContainSingle().Which.TokenHash.Should().Be(InviteTokens.Hash(BasketFactory.BootstrapToken)).And.NotContain(BasketFactory.BootstrapToken);
    }

    [Fact]
    public async Task Without_a_long_enough_bootstrap_token_nobody_can_become_owner()
    {
        await using var factory = new BasketFactory { Bootstrap = "too-short" };
        var client = factory.NewBrowser();

        (await CheckAsync(client, "too-short")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await factory.WithDb(db => db.Invites.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_owner_registers_with_the_bootstrap_link_and_it_cannot_be_used_again()
    {
        await using var factory = new BasketFactory();
        var client = factory.NewBrowser();

        var (_, session) = await factory.RegisterAsync(client, BasketFactory.BootstrapToken, "  Denys  ");

        session.GetProperty("user").GetProperty("displayName").GetString().Should().Be("Denys");
        session.GetProperty("user").GetProperty("role").GetString().Should().Be("owner");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/session");
        me.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        (await CheckAsync(factory.NewBrowser(), BasketFactory.BootstrapToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var second = await factory.NewBrowser().PostAsJsonAsync("/api/auth/register/options", new { token = BasketFactory.BootstrapToken, displayName = "Mallory" });
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_second_owner_cannot_appear_even_if_a_bootstrap_link_is_still_open()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await factory.WithDb(async db =>
        {
            db.Invites.Add(new Invite
            {
                Id = Guid.NewGuid(),
                TokenHash = InviteTokens.Hash("second-owner-link-0123456789abcdef0123456789"),
                Role = Roles.Owner,
                CreatedAt = factory.Clock.GetUtcNow(),
                ExpiresAt = factory.Clock.GetUtcNow().AddHours(1),
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var attacker = factory.NewBrowser();
        var options = await attacker.PostAsJsonAsync("/api/auth/register/options", new { token = "second-owner-link-0123456789abcdef0123456789", displayName = "Eve" });
        var passkey = new FakeAuthenticator(BasketFactory.Origin);
        var done = await attacker.PostAsJsonAsync("/api/auth/register", new
        {
            token = "second-owner-link-0123456789abcdef0123456789",
            credentialJson = passkey.Create(await options.Content.ReadAsStringAsync()),
        });

        done.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetFromJsonAsync<JsonElement[]>("/api/members"))!.Should().ContainSingle();
    }

    [Fact]
    public async Task An_owner_mints_a_24_hour_link_whose_token_rides_in_the_fragment_and_is_stored_hashed()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();

        var response = await owner.PostAsJsonAsync("/api/invites", new { label = "Her" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        body.GetProperty("link").GetString().Should().Be($"{BasketFactory.Origin}/join#{token}");
        body.GetProperty("expiresAt").GetDateTimeOffset().Should().BeCloseTo(factory.Clock.GetUtcNow().AddHours(24), TimeSpan.FromSeconds(1));
        token.Length.Should().BeGreaterThanOrEqualTo(43);
        var stored = await factory.WithDb(db => db.Invites.Where(i => i.Label == "Her").SingleAsync());
        stored.TokenHash.Should().Be(InviteTokens.Hash(token));
        stored.TokenHash.Should().NotContain(token);
    }

    [Fact]
    public async Task An_invite_works_once_and_the_member_is_signed_in_with_the_member_role()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (_, token) = await BasketFactory.MintInviteAsync(owner, "Her");

        var her = factory.NewBrowser();
        var (_, session) = await factory.RegisterAsync(her, token, "Her");

        session.GetProperty("user").GetProperty("role").GetString().Should().Be("member");
        (await her.GetAsync("/api/list")).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await CheckAsync(factory.NewBrowser(), token);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var replay = await factory.NewBrowser().PostAsJsonAsync("/api/auth/register/options", new { token, displayName = "Someone else" });
        replay.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var consumed = await factory.WithDb(db => db.Invites.SingleAsync(i => i.Label == "Her"));
        consumed.ConsumedAt.Should().NotBeNull();
        consumed.ConsumedByUserId.Should().NotBeNull();
    }

    [Fact]
    public async Task An_invite_is_dead_after_24_hours_but_still_alive_just_before()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (_, token) = await BasketFactory.MintInviteAsync(owner);
        var stranger = factory.NewBrowser();

        factory.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        (await CheckAsync(stranger, token)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromSeconds(2));
        (await CheckAsync(stranger, token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var options = await stranger.PostAsJsonAsync("/api/auth/register/options", new { token, displayName = "Late" });
        options.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_link_that_expires_between_the_two_registration_steps_cannot_finish()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (_, token) = await BasketFactory.MintInviteAsync(owner);
        var her = factory.NewBrowser();
        var passkey = new FakeAuthenticator(BasketFactory.Origin);
        var options = await her.PostAsJsonAsync("/api/auth/register/options", new { token, displayName = "Her" });
        var credential = passkey.Create(await options.Content.ReadAsStringAsync());

        factory.Clock.Advance(TimeSpan.FromHours(25));
        var done = await her.PostAsJsonAsync("/api/auth/register", new { token, credentialJson = credential });

        done.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await factory.WithDb(db => db.Users.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_revoked_invite_stops_working_and_a_used_one_cannot_be_revoked()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (id, token) = await BasketFactory.MintInviteAsync(owner);

        (await owner.DeleteAsync($"/api/invites/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CheckAsync(factory.NewBrowser(), token)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var (usedId, usedToken) = await BasketFactory.MintInviteAsync(owner);
        await factory.RegisterAsync(factory.NewBrowser(), usedToken, "Her");
        (await owner.DeleteAsync($"/api/invites/{usedId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pending_invites_list_leaves_out_used_expired_and_bootstrap_links()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (_, used) = await BasketFactory.MintInviteAsync(owner, "used");
        await factory.RegisterAsync(factory.NewBrowser(), used, "Her");
        await BasketFactory.MintInviteAsync(owner, "old");
        factory.Clock.Advance(TimeSpan.FromHours(25));
        await BasketFactory.MintInviteAsync(owner, "fresh");

        var pending = await owner.GetFromJsonAsync<JsonElement[]>("/api/invites");

        pending!.Select(p => p.GetProperty("label").GetString()).Should().Equal("fresh");
    }

    [Fact]
    public async Task Of_two_simultaneous_registrations_on_one_link_exactly_one_wins()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (id, _) = await BasketFactory.MintInviteAsync(owner);

        async Task<bool> TryConsume()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var userId = Guid.NewGuid();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new AppUser { Id = userId, UserName = userId.ToString("N"), NormalizedUserName = userId.ToString("N").ToUpperInvariant(), DisplayName = "x", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return await scope.ServiceProvider.GetRequiredService<InviteService>().TryConsumeAsync(id, userId, CancellationToken.None);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TryConsume()));

        results.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task Cleanup_removes_week_old_dead_invites_and_keeps_recent_and_pending_ones()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await BasketFactory.MintInviteAsync(owner, "ancient");
        factory.Clock.Advance(TimeSpan.FromDays(9));
        await BasketFactory.MintInviteAsync(owner, "pending");

        var removed = await factory.WithDb(async db =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<InviteService>().PurgeAsync(CancellationToken.None);
        });

        removed.Should().BeGreaterThanOrEqualTo(1);
        var labels = await factory.WithDb(db => db.Invites.Select(i => i.Label).ToListAsync());
        labels.Should().Contain("pending").And.NotContain("ancient");
    }

    [Fact]
    public async Task Garbage_tokens_get_the_same_answer_as_expired_ones()
    {
        await using var factory = new BasketFactory();
        var client = factory.NewBrowser();

        var unknown = await CheckAsync(client, "x");
        var long_ = await CheckAsync(client, new string('a', 5000));
        var empty = await client.PostAsJsonAsync("/api/auth/invite", new { token = (string?)null });

        foreach (var response in new[] { unknown, long_, empty })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().Contain("invite_invalid");
        }
    }

    [Fact]
    public async Task A_registration_ceremony_from_another_origin_is_rejected_and_leaves_the_link_unspent()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (_, token) = await BasketFactory.MintInviteAsync(owner);
        var client = factory.NewBrowser();
        var passkey = new FakeAuthenticator(BasketFactory.Origin);
        var options = await client.PostAsJsonAsync("/api/auth/register/options", new { token, displayName = "Phish" });

        var done = await client.PostAsJsonAsync("/api/auth/register", new
        {
            token,
            credentialJson = passkey.Create(await options.Content.ReadAsStringAsync(), originOverride: "https://basket.test.evil.example"),
        });

        done.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CheckAsync(factory.NewBrowser(), token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Registration_needs_a_display_name()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (_, token) = await BasketFactory.MintInviteAsync(owner);

        var response = await factory.NewBrowser().PostAsJsonAsync("/api/auth/register/options", new { token, displayName = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_member_signs_out_and_back_in_with_the_passkey_and_a_stranger_cannot()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var (her, passkey) = await factory.MemberAsync(owner, "Her");

        (await her.PostAsync("/api/auth/sign-out", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await her.GetAsync("/api/list")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await factory.SignInAsync(her, passkey)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await her.GetAsync("/api/list")).StatusCode.Should().Be(HttpStatusCode.OK);

        var stranger = factory.NewBrowser();
        var response = await factory.SignInAsync(stranger, new FakeAuthenticator(BasketFactory.Origin));
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        (await stranger.GetAsync("/api/list")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sign_in_answers_a_garbage_credential_without_saying_why()
    {
        await using var factory = new BasketFactory();
        var client = factory.NewBrowser();

        var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { credentialJson = "{\"id\":\"AAAA\"}" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sign_in_failed");
    }

    [Fact]
    public async Task The_owner_sees_the_household_members()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync("Denys");
        await factory.MemberAsync(owner, "Her");

        var members = await owner.GetFromJsonAsync<JsonElement[]>("/api/members");

        members!.Select(m => (m.GetProperty("displayName").GetString(), m.GetProperty("role").GetString()))
            .Should().Equal(("Denys", "owner"), ("Her", "member"));
    }
}
