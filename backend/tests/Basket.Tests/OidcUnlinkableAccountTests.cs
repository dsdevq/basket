using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

/// <summary>
/// Which unlinked accounts hold back the creation of a new provider Member. Only one that can still link does: its holder can open a
/// session and link it. A locked-out account cannot, so it must not block. This class has its own app so it controls every unlinked account.
/// </summary>
public sealed class OidcUnlinkableAccountTests(OidcBasketFactory factory) : OidcTestBase(factory), IClassFixture<OidcBasketFactory>
{
    private async Task LockOutAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        (await users.SetLockoutEndDateAsync((await users.FindByIdAsync(id.ToString()))!, DateTimeOffset.UtcNow.AddDays(1))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_locked_out_unlinked_account_does_not_block_a_new_member_but_a_usable_one_does()
    {
        var locked = await SeedAccountAsync(null, Roles.Member, "Locked");
        await LockOutAsync(locked.Id);

        var created = _factory.NewBrowser();
        var first = await SignInAtProviderAsync(created, new ProviderPerson("sub-" + Unique(), $"{Unique()}@example.test", true, "Ana", AppRole));

        SetsSession(first).Should().BeTrue();
        (await SessionAsync(created)).GetProperty("user").GetProperty("id").GetGuid().Should().NotBe(locked.Id);

        await SeedAccountAsync(null, Roles.Member, "Usable");

        var blocked = _factory.NewBrowser();
        var second = await SignInAtProviderAsync(blocked, new ProviderPerson("sub-" + Unique(), $"{Unique()}@example.test", true, "Bo", AppRole));

        SetsSession(second).Should().BeFalse();
        second.Headers.Location!.ToString().Should().Be(LinkRequired);
    }
}
