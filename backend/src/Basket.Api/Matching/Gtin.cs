namespace Basket.Api.Matching;

/// <summary>A product barcode (GTIN-8, UPC-A/GTIN-12, EAN-13 or GTIN-14), checked and kept in one 14-digit form.</summary>
public static class Gtin
{
    /// <summary>
    /// Digits only (spaces and hyphens are tolerated), a known length and a correct check digit. The result is padded with
    /// leading zeros to 14 digits, so the same product written as UPC-A or as EAN-13 compares equal.
    /// </summary>
    public static bool TryNormalize(string? raw, out string gtin14)
    {
        gtin14 = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var digits = raw.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (digits.Length is not (8 or 12 or 13 or 14) || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        // Weights 3,1,3,1... from the digit next to the check digit; the sum plus the check digit is a multiple of 10.
        var sum = 0;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            var digit = digits[digits.Length - 2 - i] - '0';
            sum += digit * (i % 2 == 0 ? 3 : 1);
        }

        if ((10 - (sum % 10)) % 10 != digits[^1] - '0')
        {
            return false;
        }

        gtin14 = digits.PadLeft(14, '0');
        return true;
    }
}
