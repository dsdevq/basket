namespace Basket.Tests.Support;

/// <summary>Pages recorded from the shops' sites (see the comment at the top of each file for when and from where).</summary>
public static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
