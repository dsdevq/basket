using Basket.Api.Data;
using Basket.Api.Prices;
using Microsoft.AspNetCore.DataProtection;

namespace Basket.Api.Cards;

/// <summary>What a card number has to look like for each code, and the code a shop's card normally uses.</summary>
public static class CardNumbers
{
    public const int MaxCode128Length = 48;
    public const int MaxQrLength = 300;

    /// <summary>
    /// The code the shop's own card or app shows, so that adding a card needs only the number. Lidl Plus is a QR code
    /// in the app; the other three are linear codes, which Code 128 draws for any number of digits. The owner can
    /// change it per card when a card turns out to carry another code.
    /// </summary>
    public static CardFormat DefaultFormat(Store store) => store == Store.Lidl ? CardFormat.Qr : CardFormat.Code128;

    public static string FormatName(CardFormat format) => format switch
    {
        CardFormat.Code128 => "code128",
        CardFormat.Qr => "qr",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool TryParseFormat(string? value, out CardFormat format)
    {
        format = default;
        foreach (var candidate in Enum.GetValues<CardFormat>())
        {
            if (string.Equals(FormatName(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The number as it will be drawn, or null when <paramref name="raw"/> cannot be drawn as <paramref name="format"/>.
    /// Spaces and dashes typed for readability are dropped for the linear codes; a QR code keeps the text as given.
    /// </summary>
    public static string? Normalize(CardFormat format, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        switch (format)
        {
            case CardFormat.Qr:
                var text = raw.Trim();
                return text.Length <= MaxQrLength && !text.Any(char.IsControl) ? text : null;

            default:
                var compact = Compact(raw);
                return compact.Length is >= 4 and <= MaxCode128Length && compact.All(c => c is >= '!' and <= '~') ? compact : null;
        }
    }

    /// <summary>The number with all but its last four characters hidden, for lists that must not carry it whole.</summary>
    public static string Mask(string number) => number.Length <= 4 ? new string('•', number.Length) : "•••• " + number[^4..];

    private static string Compact(string raw) => string.Concat(raw.Where(c => !char.IsWhiteSpace(c) && c != '-'));
}

/// <summary>Encrypts and decrypts card numbers with the app's Data Protection keys (the same keys as the cookie).</summary>
public sealed class CardNumberProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("basket.loyalty-card.number.v1");

    public string Protect(string number) => _protector.Protect(number);

    /// <summary>The number, or null when the stored value cannot be read (keys lost or replaced).</summary>
    public string? Unprotect(string stored)
    {
        try
        {
            return _protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
