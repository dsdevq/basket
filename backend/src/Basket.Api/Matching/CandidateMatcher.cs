using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Basket.Api.Matching;

/// <summary>
/// What the matcher knows about a catalog product: its name (which may state a size) and its barcode, if any. <see cref="Category"/>
/// is the shop's category path ("Chilled Food &gt; Milk") when a page of the product has one; <see cref="Size"/> is the size when it
/// is known apart from the name (a catalog row states it in a field of its own), otherwise it is read from the name.
/// </summary>
public sealed record ProductFacts(string Name, string? Barcode, string? Category = null, ProductSize? Size = null);

/// <summary>
/// One result of a shop's search or one row of a shop's catalog: the page, its title as the shop shows it and, when it carried one,
/// its barcode, category path and size (read from the title when not given).
/// </summary>
public sealed record CandidateFacts(string? Title, string? Barcode, string? Category = null, ProductSize? Size = null);

/// <summary>Why a candidate was proposed, strongest first.</summary>
public static class MatchBasis
{
    /// <summary>Both sides carry the same barcode.</summary>
    public const string Barcode = "barcode";

    /// <summary>The names agree and both state the same size.</summary>
    public const string NameAndSize = "name_size";

    /// <summary>The names agree closely and at least one side states no size, so the size rule could not be checked.</summary>
    public const string NameOnly = "name";

    /// <summary>
    /// The generic names agree and both state the same size, but only once each shop's own-label words (its name and its
    /// store brands) are left out: the same kind of product under another shop's label.
    /// </summary>
    public const string Generic = "generic";
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
/// <item>When rule 2 finds no match with both sizes stated, the names are compared again as <i>generic</i> names: the words of each shop's own label
/// (<see cref="OwnLabel"/>: "Milbona", "Tesco", "Dunnes Stores Simply Better", ...) are left out of both, so the same kind of
/// product under two shops' labels agrees ("Tesco whole milk 2L", "Milbona whole milk 2L"). It needs both sizes stated and equal
/// (a 6 x 330 ml pack is not 330 ml), the same containment as rule 2, and ranks below it (<see cref="MatchBasis.Generic"/>).
/// A manufacturer's brand is never left out: "Heinz baked beans" is not "Branston baked beans".</item>
/// <item>Categories, when both sides have one, are a second opinion. Shops file things differently, so two category paths that
/// share a word agree and add 5 to the score; paths with no word in common disagree, which rejects the loose matches (a generic one,
/// and one with a size missing) but not one that states the same name and size.</item>
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

        var wanted = product.Size ?? ProductSize.Parse(product.Name);
        var offered = candidate.Size ?? ProductSize.Parse(candidate.Title);
        if (wanted is not null && offered is not null && wanted != offered)
        {
            return null;
        }

        var agreement = CategoryAgreement(product.Category, candidate.Category);
        var bonus = agreement == Agreement.Agree ? CategoryBonus : 0;
        var similarity = Similarity(Words(product.Name), Words(candidate.Title));
        if (wanted is not null && offered is not null)
        {
            if (Contains(Words(product.Name), Words(candidate.Title)))
            {
                return new MatchVerdict(MatchBasis.NameAndSize, 50 + (int)Math.Round(40 * similarity, MidpointRounding.AwayFromZero) + bonus);
            }

            return agreement == Agreement.Disagree ? null : GenericVerdict(product.Name, candidate.Title, bonus);
        }

        return similarity >= NameOnlyThreshold && agreement != Agreement.Disagree
            ? new MatchVerdict(MatchBasis.NameOnly, 20 + (int)Math.Round(25 * similarity, MidpointRounding.AwayFromZero) + bonus)
            : null;
    }

    /// <summary>
    /// Rule 4: the names with every own-label word left out, compared as rule 2 compares them. The size rule has already passed
    /// (both stated, equal) when this runs. Scores 25-49, below any <see cref="MatchBasis.NameAndSize"/>.
    /// </summary>
    private static MatchVerdict? GenericVerdict(string productName, string candidateTitle, int bonus)
    {
        var wanted = GenericWords(productName);
        var offered = GenericWords(candidateTitle);
        if (!Contains(wanted, offered))
        {
            return null;
        }

        return new MatchVerdict(MatchBasis.Generic, 25 + (int)Math.Round(20 * Similarity(wanted, offered), MidpointRounding.AwayFromZero) + bonus);
    }

    /// <summary>Added to the score of a match whose category paths share a word.</summary>
    public const int CategoryBonus = 5;

    internal enum Agreement
    {
        /// <summary>One side or both have no category, so there is nothing to compare.</summary>
        Unknown,

        Agree,

        Disagree,
    }

    /// <summary>
    /// Whether two shops' category paths ("Chilled Food &gt; Milk", "Fresh &gt; Dairy &gt; Milk") name the same part of the shop: they
    /// agree when they share a meaningful word (lower case, accents folded, plural "s" dropped, "food" and the like ignored).
    /// Taxonomies differ between shops, so sharing nothing is only <see cref="Agreement.Disagree"/> when both paths have words.
    /// </summary>
    internal static Agreement CategoryAgreement(string? a, string? b)
    {
        var left = CategoryWords(a);
        var right = CategoryWords(b);
        if (left.Count == 0 || right.Count == 0)
        {
            return Agreement.Unknown;
        }

        return left.Overlaps(right) ? Agreement.Agree : Agreement.Disagree;
    }

    private static HashSet<string> CategoryWords(string? path) =>
        path is null ? [] : WordList(path, dropOwnLabel: false).Where(w => !CategoryFiller.Contains(w)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The first and the last generic word of a name: the kind of thing it is, for narrowing a catalog before matching.</summary>
    public static IReadOnlyList<string> KeyWords(string name)
    {
        var words = WordList(name, dropOwnLabel: true);
        return words.Count == 0 ? [] : words.Count == 1 ? [words[0]] : [words[0], words[^1]];
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
    internal static HashSet<string> Words(string name) => WordList(name, dropOwnLabel: false).ToHashSet(StringComparer.Ordinal);

    /// <summary><see cref="Words"/> without the words of a shop's own label (<see cref="OwnLabel"/>): what the product is, not whose.</summary>
    internal static HashSet<string> GenericWords(string name) => WordList(name, dropOwnLabel: true).ToHashSet(StringComparer.Ordinal);

    /// <summary>The words of <see cref="Words"/> in the order the name gives them, each once.</summary>
    private static List<string> WordList(string name, bool dropOwnLabel)
    {
        var folded = new StringBuilder(name.Length);
        foreach (var ch in ProductSize.StripSize(name).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(ch));
            }
        }

        var text = dropOwnLabel ? OwnLabel().Replace(folded.ToString(), " ") : folded.ToString();
        var words = new List<string>();
        foreach (var raw in NonWord().Split(text))
        {
            var word = raw.Length > 3 && raw.EndsWith('s') && !raw.EndsWith("ss", StringComparison.Ordinal) ? raw[..^1] : raw;
            if (word.Length > 1 && !Filler.Contains(word) && !words.Contains(word))
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

    private static readonly HashSet<string> CategoryFiller = new(StringComparer.Ordinal)
    {
        "food", "fresh", "grocery", "all", "product", "shop", "range", "other",
    };

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    /// <summary>
    /// The shops' own labels, on folded lower-case text: the shop's name and its store brands, longest phrase first so
    /// "dunnes stores simply better" goes whole. Only labels that are unmistakably a shop's own are listed; a name that merely
    /// contains a common word ("better", "selected") is left alone unless the whole phrase is a label.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:dunnes\s+stores\s+simply\s+better|dunnes\s+stores\s+best|dunnes\s+stores|dunnes|specially\s+selected|everyday\s+essentials|tesco\s+finest|nature'?s\s+pick|"
        + @"tesco|lidl|aldi|milbona|deluxe|pilos|cien|belbake|sondey|alesto|freeway|ferndale|brooklea|cowbelle|emporium|mamia|choceur|lacura|oakridge)\b")]
    private static partial Regex OwnLabel();
}
