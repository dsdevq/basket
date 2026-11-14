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

    /// <summary>Lidl Plus's receipts service (the receipts list of the app).</summary>
    public string TicketsBase { get; set; } = "https://tickets.lidlplus.com";

    /// <summary>The country the Lidl Plus account belongs to; Lidl keeps receipts per country.</summary>
    public string Country { get; set; } = "IE";

    /// <summary>The language the receipts are asked for. Lidl accepts two letters at most; a longer tag such as <c>en-IE</c> is cut to <c>en</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// The version of Lidl's app the receipts requests say they come from. Lidl turns away a made-up one ("999.99.9" has its stream reset)
    /// and accepts a real release, so this is kept near the current one; raise it if Lidl starts turning requests away again.
    /// </summary>
    public string AppVersion { get; set; } = "16.40.2";

    /// <summary>The pause between two requests to Lidl while receipts are pulled, so a first pull of years of receipts stays polite.</summary>
    public int ReceiptPauseMilliseconds { get; set; } = 500;

    /// <summary>The most pages of the receipts list one pull reads (about ten receipts a page).</summary>
    public int MaxReceiptPages { get; set; } = 50;

    /// <summary>The most new receipts one pull opens; a longer history is taken over the next nights.</summary>
    public int MaxNewReceiptsPerPull { get; set; } = 30;
}
