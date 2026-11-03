namespace Basket.Api.Data;

/// <summary>One line of the shared shopping list. Free text in M1; store matching arrives in M2.</summary>
public sealed class ListItem
{
    public const int MaxTextLength = 200;

    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;

    public bool IsChecked { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public Guid AddedByUserId { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public Guid? CheckedByUserId { get; set; }
}
