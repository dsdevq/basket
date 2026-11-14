using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Prices;
using Basket.Api.Walk;
using Basket.Tests.Support;
using FluentAssertions;

namespace Basket.Tests;

public sealed class SeededSectionOrderTests
{
    [Theory]
    [InlineData(Store.Lidl)]
    [InlineData(Store.Aldi)]
    [InlineData(Store.Tesco)]
    [InlineData(Store.Dunnes)]
    public void Every_chain_has_a_seeded_order_that_starts_with_fruit_then_meat_and_ends_with_other(Store store)
    {
        var order = SeededSectionOrder.For(store);

        order.Should().OnlyHaveUniqueItems();
        order[0].Should().Be("Fruit & veg");
        order.ToList().IndexOf("Meat & fish").Should().BeGreaterThan(0);
        order[^1].Should().Be(SeededSectionOrder.Other);
    }

    [Theory]
    [InlineData("Fresh Food > Fruit & Vegetables", "Fruit & veg")]
    [InlineData("Fresh Food > Milk", "Dairy & eggs")]
    [InlineData("Chilled Food > Cheese", "Dairy & eggs")]
    [InlineData("Meat, Poultry & Fish > Chicken", "Meat & fish")]
    [InlineData("Frozen Food > Ice cream", "Frozen")]
    [InlineData("Fresh Food > Steak", "Other")]
    [InlineData("Fruit > Watermelon", "Fruit & veg")]
    [InlineData("Fresh Food > Vegan Cheese", "Dairy & eggs")]
    [InlineData("Fresh Food > Butternut Squash", "Other")]
    [InlineData("Fresh Food > Eggplant", "Other")]
    [InlineData("Cupboard > Rolled Oats", "Cupboard")]
    [InlineData("Household > Carpet Cleaner", "Household")]
    [InlineData("Fresh Food > Eggs", "Dairy & eggs")]
    [InlineData("Fresh Food", "Other")]
    [InlineData("Gift cards", "Other")]
    [InlineData(null, "Other")]
    [InlineData("  ", "Other")]
    public void A_breadcrumb_maps_to_its_innermost_matching_section(string? category, string section) =>
        SeededSectionOrder.SectionOf(category).Should().Be(section);

    [Fact]
    public void Fruit_ranks_before_meat_and_an_uncategorised_product_ranks_last()
    {
        SeededSectionOrder.Rank(Store.Lidl, "Fresh Food > Fruit").Should().BeLessThan(SeededSectionOrder.Rank(Store.Lidl, "Meat > Beef"));
        SeededSectionOrder.Rank(Store.Lidl, null).Should().Be(SeededSectionOrder.For(Store.Lidl).Count - 1);
    }

    [Fact]
    public async Task The_seeded_order_of_all_four_chains_is_read_only_for_signed_in_people()
    {
        await using var factory = new BasketFactory();
        (await factory.CreateClient().GetAsync("/api/walk/seeded")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (owner, _) = await factory.OwnerAsync();
        var body = await owner.GetFromJsonAsync<JsonElement>("/api/walk/seeded");

        body.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("store").GetString())
            .Should().Equal("lidl", "aldi", "tesco", "dunnes");
    }
}
