using System.Text.RegularExpressions;
using Basket.Api.Data;
using Basket.Api.Prices;

namespace Basket.Api.Walk;

/// <summary>
/// The typical order the sections of a shop come as you walk it, one per chain. It is the fallback of walk order: used for a
/// store until the order learned from its trips is enough. Nobody enters it; a person never types an aisle. Borrowed from the
/// default aisle order of AnyList and the Tesco app.
/// </summary>
public static class SeededSectionOrder
{
    /// <summary>The section a product with no category, or one no section claims, falls in: after every named section.</summary>
    public const string Other = "Other";

    /// <summary>
    /// Checked in this order, so "ice cream" is frozen before "cream" is dairy. A word counts only as a whole word of the
    /// breadcrumb, with an optional plural, so "tea" is not found in "steak" nor "egg" in "eggplant".
    /// </summary>
    private static readonly (string Section, Regex Words)[] Keywords = Array.ConvertAll<(string Section, string[] Words), (string Section, Regex Words)>(
    [
        ("Frozen", ["frozen", "ice cream"]),
        ("Fruit & veg", ["fruit", "veg", "salad", "potato", "herb"]),
        ("Bakery", ["bakery", "bread", "baker", "cake", "roll"]),
        ("Meat & fish", ["meat", "fish", "poultry", "chicken", "beef", "pork", "seafood", "bacon", "sausage"]),
        ("Dairy & eggs", ["dairy", "milk", "cheese", "yogurt", "yoghurt", "butter", "egg", "cream", "chilled"]),
        ("Cupboard", ["cupboard", "pantry", "tinned", "pasta", "rice", "cereal", "sauce", "snack", "biscuit", "confectionery", "sweet", "chocolate"]),
        ("Drinks", ["drink", "beverage", "water", "juice", "wine", "beer", "cider", "spirit", "tea", "coffee", "soft"]),
        ("Household", ["household", "cleaning", "laundry", "paper", "kitchen"]),
        ("Health & beauty", ["health", "beauty", "toiletries", "baby", "pharmacy", "personal care"]),
        ("Pet", ["pet"]),
    ],
        k => (k.Section, new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', k.Words.Select(Regex.Escape))})(?:e?s)?(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));

    private static readonly IReadOnlyDictionary<Store, string[]> Orders = new Dictionary<Store, string[]>
    {
        // Lidl: the owner's own account, fruit first and then meat.
        [Store.Lidl] = ["Fruit & veg", "Bakery", "Meat & fish", "Dairy & eggs", "Frozen", "Cupboard", "Drinks", "Household", "Health & beauty", "Pet"],
        [Store.Aldi] = ["Fruit & veg", "Bakery", "Meat & fish", "Dairy & eggs", "Frozen", "Cupboard", "Drinks", "Household", "Health & beauty", "Pet"],
        [Store.Tesco] = ["Fruit & veg", "Bakery", "Meat & fish", "Dairy & eggs", "Cupboard", "Drinks", "Frozen", "Household", "Health & beauty", "Pet"],
        [Store.Dunnes] = ["Fruit & veg", "Bakery", "Meat & fish", "Dairy & eggs", "Cupboard", "Drinks", "Frozen", "Household", "Health & beauty", "Pet"],
    };

    /// <summary>The sections of <paramref name="store"/> in the order they come, <see cref="Other"/> last.</summary>
    public static IReadOnlyList<string> For(Store store) => [.. Orders[store], Other];

    /// <summary>
    /// The section a shop's category breadcrumb ("Chilled Food &gt; Milk") belongs to, or <see cref="Other"/> when there is no
    /// category or none matches. The innermost part of the breadcrumb decides first, so "Fresh Food &gt; Milk" is dairy, not
    /// fruit; a shop's whole-store root such as "Fresh Food" only counts when nothing deeper matches.
    /// </summary>
    public static string SectionOf(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return Other;
        }

        var parts = category.Split(StoreListing.CategorySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            foreach (var (section, words) in Keywords)
            {
                if (words.IsMatch(parts[i]))
                {
                    return section;
                }
            }
        }

        return Other;
    }

    /// <summary>
    /// Where a product falls in <paramref name="store"/>'s walk: the position of its category's section, 0 first. A product with
    /// no category sorts last, in <see cref="Other"/>.
    /// </summary>
    public static int Rank(Store store, string? category) => IndexOf(For(store), SectionOf(category));

    private static int IndexOf(IReadOnlyList<string> order, string section)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == section)
            {
                return i;
            }
        }

        return order.Count - 1;
    }
}
