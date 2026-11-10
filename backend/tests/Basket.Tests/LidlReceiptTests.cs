using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.LidlPlus;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Basket.Tests.Support.FakeLidlPlusTickets;

namespace Basket.Tests;

/// <summary>
/// Pulling Lidl Plus receipts in: each becomes a closed trip on its day, its lines become shelf products, and what was paid is kept with
/// its day. Lidl is a script (<see cref="FakeLidlPlusTickets"/>); no test calls it.
/// </summary>
public sealed class LidlReceiptTests
{
    private const string Token = "A1B2C3D4E5F60718293A4B5C6D7E8F90-1";

    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 18, 30, 0, TimeSpan.Zero);

    private static async Task<HttpClient> ConnectedOwnerAsync(BasketFactory factory)
    {
        var (owner, _) = await factory.OwnerAsync();
        (await owner.PutAsJsonAsync("/api/lidl-plus/connection", new { refreshToken = Token })).StatusCode.Should().Be(HttpStatusCode.Created);
        return owner;
    }

    private static async Task<ReceiptImportResult> PullAsync(BasketFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LidlReceiptImport>().PullAsync(CancellationToken.None);
    }

    private static async Task<JsonElement> TripsAsync(HttpClient client, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>("/api/trips" + query);

    private static async Task<JsonElement[]> ProductsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/products")).GetProperty("products").EnumerateArray().ToArray();

    private static JsonElement LidlOf(JsonElement product) =>
        product.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "lidl");

    private static LidlReceipt Weekly(string id, DateTimeOffset date, decimal milk = 1.29m) =>
        Receipt(id, date, milk + 2.49m, Line("Whole milk 2L", milk), Line("Brown bread", 2.49m));

    [Fact]
    public async Task A_receipt_becomes_a_closed_trip_on_its_day_with_its_lines_and_total()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday));

        var result = await PullAsync(factory);

        result.Receipts.Should().Be(1);
        var trips = (await TripsAsync(owner)).GetProperty("trips").EnumerateArray().ToArray();
        var trip = trips.Should().ContainSingle().Subject;
        trip.GetProperty("closedAt").GetDateTimeOffset().Should().Be(Monday);
        trip.GetProperty("source").GetString().Should().Be("lidl_plus");
        trip.GetProperty("total").GetDecimal().Should().Be(3.78m);
        trip.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString())
            .Should().BeEquivalentTo("Whole milk 2L", "Brown bread");
        factory.Receipts.Tokens.Should().OnlyContain(t => t == "access-token");
    }

    [Fact]
    public async Task The_same_receipt_is_never_opened_or_filed_twice()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday));
        await PullAsync(factory);
        factory.Receipts.Opened.Clear();

        var again = await PullAsync(factory);

        again.Receipts.Should().Be(0);
        factory.Receipts.Opened.Should().BeEmpty();
        (await TripsAsync(owner)).GetProperty("trips").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_receipt_a_later_pull_finds_is_added_and_the_older_ones_stay_as_they_were()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday));
        await PullAsync(factory);
        factory.Receipts.Add(Weekly("R-2", Monday.AddDays(7)));

        (await PullAsync(factory)).Receipts.Should().Be(1);

        var trips = (await TripsAsync(owner)).GetProperty("trips").EnumerateArray().ToArray();
        trips.Select(t => t.GetProperty("closedAt").GetDateTimeOffset()).Should().Equal(Monday.AddDays(7), Monday);
        factory.Receipts.Opened.Should().Equal("R-1", "R-2");
    }

    [Fact]
    public async Task Lines_become_shelf_products_and_a_product_with_the_same_name_is_reused_whatever_its_letter_case()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        var existing = await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var milk = new Product { Id = Guid.NewGuid(), Name = "whole MILK 2l", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(milk);
            await db.SaveChangesAsync();
            return milk.Id;
        });
        factory.Receipts.Add(Weekly("R-1", Monday));

        var result = await PullAsync(factory);

        result.Products.Should().Be(1);
        var products = await ProductsAsync(owner);
        products.Select(p => p.GetProperty("name").GetString()).Should().BeEquivalentTo("whole MILK 2l", "Brown bread");
        var trip = (await TripsAsync(owner)).GetProperty("trips")[0];
        trip.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("text").GetString() == "Whole milk 2L");
        await factory.WithDb(async db =>
        {
            var items = await db.TripItems.AsNoTracking().ToListAsync();
            items.Single(i => i.Text == "Whole milk 2L").ProductId.Should().Be(existing);
            items.Should().OnlyContain(i => i.Store == Store.Lidl && i.ProductId != null);
            (await db.Products.AsNoTracking().Where(p => p.Name == "Brown bread").Select(p => p.AddedByUserId).SingleAsync())
                .Should().Be(await db.Users.Select(u => u.Id).FirstAsync());
            return 0;
        });
    }

    [Fact]
    public async Task The_last_paid_price_and_its_day_reach_the_products_list_and_the_plan()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday, milk: 1.29m)).Add(Weekly("R-2", Monday.AddDays(7), milk: 1.39m));
        await PullAsync(factory);

        var milk = (await ProductsAsync(owner)).Single(p => p.GetProperty("name").GetString() == "Whole milk 2L");
        var paid = LidlOf(milk).GetProperty("lastPaid");
        paid.GetProperty("state").GetString().Should().Be("known");
        paid.GetProperty("price").GetDecimal().Should().Be(1.39m);
        paid.GetProperty("paidOn").GetString().Should().Be("2026-11-09");

        var pick = await owner.PostAsJsonAsync("/api/list/items", new { productId = milk.GetProperty("id").GetGuid(), store = "lidl" });
        pick.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = (await owner.GetFromJsonAsync<JsonElement>("/api/plan")).GetProperty("items").EnumerateArray().Single();
        var lastPaid = item.GetProperty("lastPaid").EnumerateArray().Should().ContainSingle().Subject;
        lastPaid.GetProperty("store").GetString().Should().Be("lidl");
        lastPaid.GetProperty("price").GetDecimal().Should().Be(1.39m);
        lastPaid.GetProperty("paidOn").GetString().Should().Be("2026-11-09");
        item.GetProperty("flag").GetString().Should().Be("no_price", "a price paid is history, not a shelf price to rank");
    }

    [Fact]
    public async Task A_product_never_bought_at_Lidl_says_so_once_Lidl_Plus_is_connected_and_not_before()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            db.Products.Add(new Product { Id = Guid.NewGuid(), Name = "Oat drink", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by });
            await db.SaveChangesAsync();
            return 0;
        });

        LidlOf((await ProductsAsync(owner)).Single()).GetProperty("lastPaid").GetProperty("state").GetString().Should().Be("not_connected");

        await owner.PutAsJsonAsync("/api/lidl-plus/connection", new { refreshToken = Token });

        LidlOf((await ProductsAsync(owner)).Single()).GetProperty("lastPaid").GetProperty("state").GetString().Should().Be("none");
    }

    [Fact]
    public async Task A_product_bought_at_Lidl_can_be_picked_from_the_Lidl_shelf_but_not_from_another_shop()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday));
        await PullAsync(factory);
        var bread = (await ProductsAsync(owner)).Single(p => p.GetProperty("name").GetString() == "Brown bread").GetProperty("id").GetGuid();

        (await owner.PostAsJsonAsync("/api/list/items", new { productId = bread, store = "lidl" })).StatusCode.Should().Be(HttpStatusCode.Created);

        var tesco = await owner.PostAsJsonAsync("/api/list/items", new { productId = bread, store = "tesco" });
        tesco.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tesco.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("not_sold_there");
    }

    [Fact]
    public async Task Same_as_last_time_on_a_receipt_puts_its_products_back_on_the_list_as_Lidl_lines()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday));
        await PullAsync(factory);
        var trip = (await TripsAsync(owner)).GetProperty("trips")[0].GetProperty("id").GetGuid();

        var reload = await owner.PostAsync($"/api/trips/{trip}/reload", null);

        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await owner.GetFromJsonAsync<JsonElement>("/api/list")).GetProperty("items").EnumerateArray().ToArray();
        items.Should().HaveCount(2).And.OnlyContain(i => i.GetProperty("store").GetString() == "lidl" && i.GetProperty("productId").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Weighed_lines_keep_the_price_of_a_kilo_and_refunds_and_zero_lines_are_not_purchases()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Receipt(
            "R-1",
            Monday,
            4.50m,
            Weighed("Bananas", 0.612m, 1.69m),
            Line("Cola 1.5L", 2.40m, quantity: 2m),
            Line("Bottle deposit", -0.30m),
            Line("Free sample", 0m),
            Line("   ", 1m)));

        await PullAsync(factory);

        var products = await ProductsAsync(owner);
        products.Select(p => p.GetProperty("name").GetString()).Should().BeEquivalentTo("Bananas", "Cola 1.5L");
        decimal Paid(string name) => LidlOf(products.Single(p => p.GetProperty("name").GetString() == name)).GetProperty("lastPaid").GetProperty("price").GetDecimal();
        Paid("Bananas").Should().Be(1.69m);
        Paid("Cola 1.5L").Should().Be(1.20m, "two for 2.40 is 1.20 each");
    }

    [Fact]
    public async Task A_line_scanned_twice_is_one_line_of_the_trip_and_two_receipts_of_one_day_keep_one_price_for_it()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Receipts
            .Add(Receipt("R-1", Monday, 2.60m, Line("Yoghurt", 1.30m), Line("Yoghurt", 1.30m)))
            .Add(Receipt("R-2", Monday.AddHours(2), 1.45m, Line("Yoghurt", 1.45m)));

        await PullAsync(factory);

        var trips = (await TripsAsync(owner)).GetProperty("trips").EnumerateArray().ToArray();
        trips.Should().HaveCount(2);
        trips.Single(t => t.GetProperty("total").GetDecimal() == 2.60m).GetProperty("items").GetArrayLength().Should().Be(1);
        await factory.WithDb(async db =>
        {
            var paid = await db.PaidPrices.AsNoTracking().ToListAsync();
            paid.Should().ContainSingle().Which.Price.Should().Be(1.45m, "the later receipt of the day is the price that stays");
            return 0;
        });
    }

    [Fact]
    public async Task The_day_a_price_was_paid_is_the_Dublin_day_of_the_receipt()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        // 23:30 UTC on 2 June is 00:30 on 3 June in Dublin (summer time).
        factory.Receipts.Add(Receipt("R-1", new DateTimeOffset(2026, 6, 2, 23, 30, 0, TimeSpan.Zero), 1m, Line("Butter", 1m)));

        await PullAsync(factory);

        await factory.WithDb(async db =>
        {
            (await db.PaidPrices.AsNoTracking().SingleAsync()).PaidOn.Should().Be(new DateOnly(2026, 6, 3));
            return 0;
        });
    }

    [Fact]
    public async Task A_long_history_is_taken_in_bites_and_the_next_pull_carries_on_where_the_last_stopped()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        for (var i = 0; i < 35; i++)
        {
            factory.Receipts.Add(Receipt($"R-{i:00}", Monday.AddDays(-i), 1m, Line($"Item {i}", 1m)));
        }

        var first = await PullAsync(factory);

        first.Receipts.Should().Be(30);
        first.Waiting.Should().Be(5);
        var newest = (await TripsAsync(owner)).GetProperty("trips").EnumerateArray().Select(t => t.GetProperty("closedAt").GetDateTimeOffset()).Max();
        newest.Should().Be(Monday, "the newest receipts are the ones taken first");

        var second = await PullAsync(factory);

        second.Receipts.Should().Be(5);
        second.Waiting.Should().Be(0);
        (await factory.WithDb(db => db.Trips.CountAsync())).Should().Be(35);
    }

    [Fact]
    public async Task When_Lidl_stops_answering_what_was_filed_stays_and_the_rest_is_taken_next_time()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday)).Add(Weekly("R-2", Monday.AddDays(1))).Add(Weekly("R-3", Monday.AddDays(2)));
        factory.Receipts.OpenFailsFor = "R-2";

        var failed = await PullAsync(factory);

        failed.Outcome.Should().Be(ReceiptImportOutcome.Unavailable);
        failed.Waiting.Should().Be(2, "the receipt Lidl failed on and the older one behind it");
        var filedFirst = await factory.WithDb(db => db.Trips.Select(t => t.ExternalId!).ToListAsync());
        filedFirst.Should().NotContain("R-2");

        factory.Receipts.OpenFailsFor = null;
        await PullAsync(factory);

        (await factory.WithDb(db => db.Trips.Select(t => t.ExternalId!).ToListAsync())).Should().BeEquivalentTo("R-1", "R-2", "R-3");
    }

    [Fact]
    public async Task A_list_that_stops_part_way_files_the_receipts_read_so_far()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        for (var i = 0; i < 12; i++)
        {
            factory.Receipts.Add(Receipt($"R-{i:00}", Monday.AddDays(-i), 1m, Line($"Item {i}", 1m)));
        }

        factory.Receipts.ListFailsFromPage = 2;
        (await PullAsync(factory)).Outcome.Should().Be(ReceiptImportOutcome.Unavailable);
        (await factory.WithDb(db => db.Trips.CountAsync())).Should().Be(10);

        factory.Receipts.ListFailsFromPage = null;
        (await PullAsync(factory)).Receipts.Should().Be(2);
    }

    [Fact]
    public async Task A_receipt_basket_cannot_read_is_skipped_and_tried_again_while_the_others_are_filed()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Weekly("R-1", Monday)).Add(Weekly("R-2", Monday.AddDays(1))).MakeUnreadable("R-1");

        var result = await PullAsync(factory);

        result.Receipts.Should().Be(1);
        result.Skipped.Should().Be(1);
        (await factory.WithDb(db => db.Trips.Select(t => t.ExternalId!).ToListAsync())).Should().Equal("R-2");
    }

    [Fact]
    public async Task Without_a_connection_a_pull_does_nothing_and_nothing_is_asked_of_Lidl()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();

        var result = await PullAsync(factory);

        result.Outcome.Should().Be(ReceiptImportOutcome.NotConnected);
        factory.Receipts.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_token_Lidl_refuses_ends_the_pull_and_asks_for_a_new_one()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        factory.Lidl.Then(LidlAuthResult.Rejected);

        var result = await PullAsync(factory);

        result.Outcome.Should().Be(ReceiptImportOutcome.NeedsNewToken);
        factory.Receipts.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task When_Lidl_cannot_be_reached_to_sign_in_nothing_is_changed()
    {
        await using var factory = new BasketFactory();
        var owner = await ConnectedOwnerAsync(factory);
        factory.Lidl.Then(LidlAuthResult.Unavailable);

        var result = await PullAsync(factory);

        result.Outcome.Should().Be(ReceiptImportOutcome.Unavailable);
        factory.Receipts.ListCalls.Should().Be(0);
        (await owner.GetFromJsonAsync<JsonElement>("/api/lidl-plus/connection")).GetProperty("state").GetString().Should().Be("connected");
    }

    [Fact]
    public async Task A_receipt_Lidl_keeps_refusing_never_holds_back_the_older_ones_and_never_uses_up_the_limit()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        for (var i = 0; i < 40; i++)
        {
            factory.Receipts.Add(Receipt($"R-{i:00}", Monday.AddDays(-i), 1m, Line($"Item {i}", 1m)));
        }

        for (var i = 0; i < 5; i++)
        {
            factory.Receipts.MakeUnreadable($"R-{i:00}");
        }

        var result = await PullAsync(factory);

        result.Outcome.Should().Be(ReceiptImportOutcome.Done);
        result.Receipts.Should().Be(30, "the five receipts basket cannot read are not counted against the limit of thirty");
        result.Skipped.Should().Be(5);
        result.Waiting.Should().Be(5);
        (await factory.WithDb(db => db.Trips.Select(t => t.ExternalId!).ToListAsync())).Should().NotContain(["R-00", "R-01", "R-02", "R-03", "R-04"]).And.Contain("R-05").And.Contain("R-34");
    }

    [Fact]
    public async Task Two_products_whose_names_differ_only_by_letter_case_do_not_stop_a_pull_and_the_first_added_is_used()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        var first = await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var older = new Product { Id = Guid.NewGuid(), Name = "Whole milk 2L", AddedAt = DateTimeOffset.UtcNow.AddDays(-2), AddedByUserId = by };
            var newer = new Product { Id = Guid.NewGuid(), Name = "WHOLE MILK 2L", AddedAt = DateTimeOffset.UtcNow.AddDays(-1), AddedByUserId = by };
            db.Products.AddRange(newer, older);
            await db.SaveChangesAsync();
            return older.Id;
        });
        factory.Receipts.Add(Weekly("R-1", Monday));

        var result = await PullAsync(factory);

        result.Outcome.Should().Be(ReceiptImportOutcome.Done);
        result.Receipts.Should().Be(1);
        await factory.WithDb(async db =>
        {
            (await db.TripItems.AsNoTracking().SingleAsync(i => i.Text == "Whole milk 2L")).ProductId.Should().Be(first);
            return 0;
        });
    }

    [Fact]
    public async Task A_receipt_found_later_that_was_printed_earlier_in_the_day_does_not_replace_the_later_price()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Receipt("R-late", Monday, 1.39m, Line("Yoghurt", 1.39m)));
        await PullAsync(factory);
        factory.Receipts.Add(Receipt("R-early", Monday.AddHours(-8), 1.29m, Line("Yoghurt", 1.29m)));

        (await PullAsync(factory)).Receipts.Should().Be(1);

        await factory.WithDb(async db =>
        {
            var paid = await db.PaidPrices.AsNoTracking().ToListAsync();
            paid.Should().ContainSingle().Which.Price.Should().Be(1.39m, "the receipt printed last is the price that stays, whatever order they were found in");
            (await db.Trips.CountAsync()).Should().Be(2);
            return 0;
        });
    }

    [Fact]
    public async Task A_receipt_found_later_that_was_printed_later_in_the_day_replaces_the_earlier_price()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        factory.Receipts.Add(Receipt("R-early", Monday.AddHours(-8), 1.29m, Line("Yoghurt", 1.29m)));
        await PullAsync(factory);
        factory.Receipts.Add(Receipt("R-late", Monday, 1.39m, Line("Yoghurt", 1.39m)));

        await PullAsync(factory);

        (await factory.WithDb(db => db.PaidPrices.AsNoTracking().ToListAsync())).Should().ContainSingle().Which.Price.Should().Be(1.39m);
    }

    [Theory]
    [InlineData(3, 9.99)]
    [InlineData(-3, 1.45)]
    public async Task A_pull_that_loses_the_race_for_a_days_price_files_its_receipt_and_the_latest_receipt_keeps_the_price(int otherHoursAfter, double expected)
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        var product = await factory.WithDb(async db =>
        {
            var by = await db.Users.Select(u => u.Id).FirstAsync();
            var yoghurt = new Product { Id = Guid.NewGuid(), Name = "Yoghurt", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = by };
            db.Products.Add(yoghurt);
            await db.SaveChangesAsync();
            return yoghurt.Id;
        });
        factory.Receipts.Add(Receipt("R-1", Monday, 1.45m, Line("Yoghurt", 1.45m)));
        factory.BeforePaidPriceInsert = () => factory.WithDb(async db =>
        {
            db.PaidPrices.Add(new PaidPrice
            {
                Id = Guid.NewGuid(),
                ProductId = product,
                Store = Store.Lidl,
                PaidOn = new DateOnly(2026, 11, 2),
                PaidAt = Monday.AddHours(otherHoursAfter),
                Price = 9.99m,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var result = await PullAsync(factory);

        result.Receipts.Should().Be(1);
        await factory.WithDb(async db =>
        {
            (await db.Trips.Select(t => t.ExternalId).ToListAsync()).Should().Equal("R-1");
            (await db.PaidPrices.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.Price.Should().Be((decimal)expected);
            return 0;
        });
    }

    [Fact]
    public async Task The_nightly_job_pulls_the_same_way_and_does_nothing_until_Lidl_Plus_is_connected()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        factory.Receipts.Add(Weekly("R-1", Monday));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LidlReceiptsJob>().RunAsync(CancellationToken.None);
        }

        (await factory.WithDb(db => db.Trips.CountAsync())).Should().Be(0);

        await owner.PutAsJsonAsync("/api/lidl-plus/connection", new { refreshToken = Token });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LidlReceiptsJob>().RunAsync(CancellationToken.None);
        }

        (await factory.WithDb(db => db.Trips.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_trip_closed_on_the_list_has_no_source_and_no_total()
    {
        await using var factory = new BasketFactory();
        var (owner, _) = await factory.OwnerAsync();
        var added = await owner.PostAsJsonAsync("/api/list/items", new { text = "Milk" });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await owner.PatchAsJsonAsync($"/api/list/items/{id}", new { @checked = true });
        await owner.PostAsync("/api/list/close", null);

        var trip = (await TripsAsync(owner)).GetProperty("trips")[0];

        trip.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
        trip.GetProperty("total").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_access_token_never_reaches_a_log()
    {
        await using var factory = new BasketFactory();
        await ConnectedOwnerAsync(factory);
        factory.Lidl.Then(FakeLidlPlusAuth.Accepts(access: "ACCESS-UNIQUE-0f3a9c1d"));
        factory.Receipts.Add(Weekly("R-1", Monday));

        await PullAsync(factory);

        factory.Receipts.Tokens.Should().Contain("ACCESS-UNIQUE-0f3a9c1d");
        factory.Logs.Lines.Should().NotContain(line => line.Contains("ACCESS-UNIQUE-0f3a9c1d", StringComparison.Ordinal));
    }
}
