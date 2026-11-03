using Basket.Api.Auth;
using Basket.Api.Data;
using Xunit;

namespace Basket.Tests;

public class UserNamesTests
{
    [Theory]
    [InlineData("  Anna  ", "Anna")]
    [InlineData("Anna\t Marie\n", "Anna Marie")]
    [InlineData("An\u0000na", "Anna")]
    public void Clean_trims_collapses_whitespace_and_drops_control_characters(string input, string expected) =>
        Assert.Equal(expected, UserNames.Clean(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0000\u0001")]
    public void Clean_gives_null_when_nothing_usable_is_left(string? input) =>
        Assert.Null(UserNames.Clean(input));

    [Fact]
    public void Clean_cuts_to_the_maximum_length() =>
        Assert.Equal(5, UserNames.Clean(new string('a', 100), 5)!.Length);
}

public class InviteTokensTests
{
    [Fact]
    public void Generated_tokens_are_unique_url_safe_and_long_enough()
    {
        var a = InviteTokens.Generate();
        var b = InviteTokens.Generate();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Fact]
    public void Hash_is_stable_and_never_the_token_itself()
    {
        var token = InviteTokens.Generate();

        Assert.Equal(InviteTokens.Hash(token), InviteTokens.Hash(token));
        Assert.NotEqual(token, InviteTokens.Hash(token));
        Assert.NotEqual(InviteTokens.Hash(token), InviteTokens.Hash(InviteTokens.Generate()));
    }
}
