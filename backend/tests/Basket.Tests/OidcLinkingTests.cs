using System.Net;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

/// <summary>
/// The passkey accounts that predate the provider. Each links to its owner's provider login the first time the owner signs in at the
/// provider with that account's session in hand; until every account is linked, nobody else is created beside them.
/// </summary>
public sealed class OidcLinkingTests(OidcBasketFactory factory) : OidcTestBase(factory), IClassFixture<OidcBasketFactory>
{
    /// <summary>
    /// A passkey member with a session. Each one costs an invite and the owner mints ten an hour, so a test that only needs another
    /// unlinked account seeds it instead.
    /// </summary>
    private async Task<(HttpClient Browser, FakeAuthenticator Passkey, Guid Id)> PasskeyMemberAsync(string name)
    {
        var (owner, _) = await _factory.OwnerAsync();
        var (browser, passkey) = await _factory.MemberAsync(owner, name);
        var id = (await SessionAsync(browser)).GetProperty("user").GetProperty("id").GetGuid();
        return (browser, passkey, id);
    }

    private static ProviderPerson Person(string sub, string email, string? name = "Ana", params string[] roles) =>
        new(sub, email, true, name, roles);

    [Fact]
    public async Task A_provider_sign_in_with_the_session_of_an_unlinked_account_links_that_account_and_no_other()
    {
        var (browser, passkey, id) = await PasskeyMemberAsync("Passkey Ana");
        var bystander = (await SeedAccountAsync(null, Roles.Member, "Bystander")).Id;
        var sub = "sub-" + Unique();
        var email = $"{Unique()}@example.test";
        var accounts = await _factory.WithDb(db => db.Users.CountAsync());

        var landed = await SignInAtProviderAsync(browser, Person(sub, email, "Name At Provider"), "/list");

        landed.Headers.Location!.ToString().Should().Be(BasketFactory.Origin + "/list");
        SetsSession(landed).Should().BeTrue();
        var user = (await SessionAsync(browser)).GetProperty("user");
        user.GetProperty("id").GetGuid().Should().Be(id);
        user.GetProperty("displayName").GetString().Should().Be("Passkey Ana");
        user.GetProperty("email").GetString().Should().Be(email);
        (await LoginsOfAsync(id)).Should().ContainSingle(l => l.LoginProvider == "lifekit" && l.ProviderKey == sub);
        (await LoginsOfAsync(bystander)).Should().BeEmpty();
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(accounts);

        // The passkey is untouched: the same account still signs in with it.
        var again = _factory.NewBrowser();
        (await _factory.SignInAsync(again, passkey)).EnsureSuccessStatusCode();
        (await SessionAsync(again)).GetProperty("user").GetProperty("id").GetGuid().Should().Be(id);
    }

    [Fact]
    public async Task The_provider_sign_in_that_follows_a_link_signs_in_to_the_same_account_without_any_session()
    {
        var (browser, _, id) = await PasskeyMemberAsync("Linked Later");
        var sub = "sub-" + Unique();
        await SignInAtProviderAsync(browser, Person(sub, $"{Unique()}@example.test"));

        var elsewhere = _factory.NewBrowser();
        var landed = await SignInAtProviderAsync(elsewhere, new ProviderPerson(sub, null, false, null));

        SetsSession(landed).Should().BeTrue();
        (await SessionAsync(elsewhere)).GetProperty("user").GetProperty("id").GetGuid().Should().Be(id);
    }

    [Fact]
    public async Task The_session_asks_for_a_link_until_the_account_has_one_and_never_for_a_stranger()
    {
        var (browser, _, _) = await PasskeyMemberAsync("Not Linked Yet");
        (await SessionAsync(browser)).GetProperty("linkRequired").GetBoolean().Should().BeTrue();

        await SignInAtProviderAsync(browser, Person("sub-" + Unique(), $"{Unique()}@example.test"));

        (await SessionAsync(browser)).GetProperty("linkRequired").GetBoolean().Should().BeFalse();
        (await SessionAsync(_factory.NewBrowser())).GetProperty("linkRequired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_new_passkey_account_is_asked_to_link_at_once()
    {
        var (owner, _) = await _factory.OwnerAsync();
        var (_, token) = await BasketFactory.MintInviteAsync(owner);

        var (_, registered) = await _factory.RegisterAsync(_factory.NewBrowser(), token, "Fresh");

        registered.GetProperty("linkRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_role_holder_with_no_account_and_no_session_is_not_created_while_an_account_is_unlinked()
    {
        await SeedAccountAsync(null, Roles.Member, "Waiting To Link");
        var browser = _factory.NewBrowser();
        var email = $"{Unique()}@example.test";
        var accounts = await _factory.WithDb(db => db.Users.CountAsync());

        var landed = await SignInAtProviderAsync(browser, Person("sub-" + Unique(), email, "Maybe The Same Person", AppRole));

        landed.Headers.Location!.ToString().Should().Be(LinkRequired);
        SetsSession(landed).Should().BeFalse();
        (await SessionAsync(browser)).GetProperty("authenticated").GetBoolean().Should().BeFalse();
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(accounts);
        (await _factory.WithDb(db => db.Users.AnyAsync(u => u.Email == email))).Should().BeFalse();
    }

    [Fact]
    public async Task A_person_without_the_role_and_no_session_is_still_just_refused_while_an_account_is_unlinked()
    {
        await SeedAccountAsync(null, Roles.Member, "Waiting To Link");

        var landed = await SignInAtProviderAsync(_factory.NewBrowser(), Person("sub-" + Unique(), $"{Unique()}@example.test", "Stranger", "some-other-app"));

        landed.Headers.Location!.ToString().Should().Be(AccessDenied);
    }

    [Fact]
    public async Task A_second_subject_cannot_take_an_account_that_is_already_linked_even_with_its_session()
    {
        var (browser, _, id) = await PasskeyMemberAsync("Taken");
        var first = "sub-" + Unique();
        await SignInAtProviderAsync(browser, Person(first, $"{Unique()}@example.test"));
        await SeedAccountAsync(null, Roles.Member, "Still Unlinked");
        var accounts = await _factory.WithDb(db => db.Users.CountAsync());

        var landed = await SignInAtProviderAsync(browser, Person("sub-" + Unique(), $"{Unique()}@example.test", "Second Person", AppRole));

        SetsSession(landed).Should().BeFalse();
        landed.Headers.Location!.ToString().Should().Be(LinkRequired);
        (await LoginsOfAsync(id)).Should().ContainSingle(l => l.ProviderKey == first);
        (await _factory.WithDb(db => db.Users.CountAsync())).Should().Be(accounts);
    }

    [Fact]
    public async Task A_subject_already_linked_to_another_account_signs_in_as_that_account_and_the_session_account_stays_unlinked()
    {
        var (linkedBrowser, _, linkedId) = await PasskeyMemberAsync("Linked");
        var sub = "sub-" + Unique();
        await SignInAtProviderAsync(linkedBrowser, Person(sub, $"{Unique()}@example.test"));
        var (otherBrowser, _, otherId) = await PasskeyMemberAsync("Other");

        var landed = await SignInAtProviderAsync(otherBrowser, new ProviderPerson(sub, null, false, null));

        SetsSession(landed).Should().BeTrue();
        (await SessionAsync(otherBrowser)).GetProperty("user").GetProperty("id").GetGuid().Should().Be(linkedId);
        (await LoginsOfAsync(otherId)).Should().BeEmpty();
        (await LoginsOfAsync(linkedId)).Should().ContainSingle(l => l.ProviderKey == sub);
    }

    [Fact]
    public async Task A_locked_out_account_is_not_linked_through_its_session()
    {
        var (browser, _, id) = await PasskeyMemberAsync("Locked");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.SetLockoutEndDateAsync((await users.FindByIdAsync(id.ToString()))!, DateTimeOffset.UtcNow.AddDays(1))).Succeeded.Should().BeTrue();
        }

        var landed = await SignInAtProviderAsync(browser, Person("sub-" + Unique(), $"{Unique()}@example.test"));

        SetsSession(landed).Should().BeFalse();
        (await LoginsOfAsync(id)).Should().BeEmpty();
    }
}
