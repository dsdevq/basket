using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Basket.Api.Matching;

/// <summary>What the matcher knows about a catalog product: its name (which may state a size) and its barcode, if any.</summary>
public sealed record ProductFacts(string Name, string? Barcode);

/// <summary>One result of a shop's search: the page, its title as the shop shows it and, when the result carried one, its barcode.</summary>
public sealed record CandidateFacts(string? Title, string? Barcode);

/// <summary>Why a candidate was proposed, strongest first.</summary>
public static class MatchBasis
{
    /// <summary>Both sides carry the same barcode.</summary>
    public const string Barcode = "barcode";

    /// <summary>The names agree and both state the same size.</summary>
    public const string NameAndSize = "name_size";

    /// <summary>The names agree closely and at least one side states no size, so the size rule could not be checked.</summary>
    public const string NameOnly = "name";
}

public sealed record MatchVerdict(string Basis, int Score);

/// <summary>
/// The rules that decide whether a shop's search result may be the equivalent of a catalog product. Pure and
/// deterministic: the same inputs always give the same verdict, there is no model or network behind it. The verdict is
/// only ever a proposal for a person to confirm; it changes no confirmed match.
/// <list type="number">
/// <item>Two valid barcodes decide it: equal is a match (score 100), different is never a match, whatever the names say.</item>
/// <item>Otherwise the names are compared as sets of words with the size left out. Both sizes stated and different is never
/// a match. Both stated and equal: the shorter name's words must all appear in the other ("Whole milk 2L" is contained in
/// "Milbona fresh whole milk 2 L"; a shop adds brand and filler words), and the score falls with every extra word
/// (Jaccard similarity). A size missing on either side needs a Jaccard similarity of at least 0.8 and ranks lower, because
/// the size rule could not be applied.</item>
/// </list>
/// </summary>
public static partial class CandidateMatcher
{
    public const double NameOnlyThreshold = 0.8;

    public static MatchVerdict? Evaluate(ProductFacts product, CandidateFacts candidate)
    {
        var productCode = Gtin.TryNormalize(product.Barcode, out var p) ? p : null;
        var candidateCode = Gtin.TryNormalize(candidate.Barcode, out var c) ? c : null;
        if (productCode is not null && candidateCode is not null)
        {
            return productCode == candidateCode ? new MatchVerdict(MatchBasis.Barcode, 100) : null;
        }

        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            return null;
        }

        var wanted = ProductSize.Parse(product.Name);
        var offered = ProductSize.Parse(candidate.Title);
        if (wanted is not null && offered is not null && wanted != offered)
        {
            return null;
        }

        var similarity = Similarity(Words(product.Name), Words(candidate.Title));
        if (wanted is not null && offered is not null)
        {
            return Contains(Words(product.Name), Words(candidate.Title))
                ? new MatchVerdict(MatchBasis.NameAndSize, 50 + (int)Math.Round(40 * similarity, MidpointRounding.AwayFromZero))
                : null;
        }

        return similarity >= NameOnlyThreshold
            ? new MatchVerdict(MatchBasis.NameOnly, 20 + (int)Math.Round(25 * similarity, MidpointRounding.AwayFromZero))
            : null;
    }

    /// <summary>True when every word of the shorter set is in the other; false when either set is empty.</summary>
    internal static bool Contains(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        return small.Count > 0 && small.All(large.Contains);
    }

    /// <summary>Jaccard similarity of two word sets: shared words over all words; 0 when either has none.</summary>
    internal static double Similarity(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>
    /// The words of a name that identify the product: lower case, accents folded, the size expression and filler words
    /// dropped, a plural "s" removed so "eggs" and "egg" agree.
    /// </summary>
    internal static HashSet<string> Words(string name)
    {
        var folded = new StringBuilder(name.Length);
        foreach (var ch in ProductSize.StripSize(name).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(ch));
            }
        }

        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in NonWord().Split(folded.ToString()))
        {
            var word = raw.Length > 3 && raw.EndsWith('s') && !raw.EndsWith("ss", StringComparison.Ordinal) ? raw[..^1] : raw;
            if (word.Length > 1 && !Filler.Contains(word))
            {
                words.Add(word);
            }
        }

        return words;
    }

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "and", "of", "with", "in", "for", "pack", "pk", "approx", "each", "per",
    };

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();
}
