namespace Basket.Api.LidlPlus;

public sealed class LidlPlusOptions
{
    public const string Section = "LidlPlus";

    /// <summary>Lidl's sign-in service, where a refresh token is exchanged for an access token.</summary>
    public string AccountsBase { get; set; } = "https://accounts.lidl.com";

    /// <summary>
    /// The client Lidl's own Android app signs in as. These two values ship inside that app and identify the app, not the
    /// owner; they are not a secret of this household. The owner's secret is the refresh token.
    /// </summary>
    public string ClientId { get; set; } = "LidlPlusNativeClient";

    public string ClientSecret { get; set; } = "secret";
}
