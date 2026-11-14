using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices;
using Basket.Api.Prices.Catalog;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Basket.Tests;

/// <summary>
/// The server's own candidate search over the combined catalog: what it proposes, what it links, what it leaves alone, and the three
/// moments it runs (a product is added, a page is confirmed, a sweep is closed). Synthetic products and shops' ids only.
/// </summary>
public sealed class ServerCandidatesTests : IClassFixture<BasketFactory>
{
    private const string Milk = "Whole milk 2L";

    private readonly BasketFactory _factory;

    public ServerCandidatesTests(BasketFactory factory) => _factory = factory;

    // ---- fixtures ----------------------------------------------------------------------------------------------

    /// <summary>An EAN-13 for the given 12 digits, with its check digit appended.</summary>
    private static string Ean13(string twelve)
    {
        var sum = twelve.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + ((10 - (sum % 10)) % 10);
    }

    private static string Url(Store store, string id) => store switch
    {
        Store.Tesco => $"https://www.tesco.ie/groceries/en-IE/products/{id}",
        Store.Aldi => $"https://www.aldi.ie/product/item-{id}",
        Store.Lidl => $"https://www.lidl.ie/p/item/p{id}",
        _ => $"https://www.dunnesstoresgrocery.com/product/item-{id}",
    };

    private async Task<HttpClient> CleanAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.ListItems.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            await db.StoreProducts.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            return 0;
        });
        return owner;
    }

    private static async Task<Guid> AddAsync(HttpClient owner, string name, string? barcode = null)
    {
        var response = await owner.PostAsJsonAsync("/api/products", new { name, barcode });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static CatalogReading Reading(
        Store store,
        string id,
        string name,
        string? size = null,
        string? brand = null,
        string? barcode = null,
        string? category = null,
        decimal? price = 1.49m)
    {
        var item = new CatalogItem(id, Url(store, id), name, brand, size, Barcode: barcode, Category: category, ImageUrl: "https://images.example.test/p.jpg", Price: price);
        CatalogReading.TryCreate(store, item, out var reading, out var reason).Should().BeTrue(reason);
        return reading!;
    }

    private Task RecordAsync(params CatalogReading[] readings) =>
        _factory.WithDb(async db =>
        {
            await new CatalogRecorder(db, new PriceRecorder(db)).RecordAsync(readings[0].Store, readings, _factory.Clock.GetUtcNow(), CancellationToken.None);
            await db.SaveChangesAsync();
            return 0;
        });

    private Task<ServerCandidateResult> RunForStoreAsync(Store store) =>
        _factory.WithDb(db => Candidates(db).RunForStoreAsync(store, CancellationToken.None));

    private static ServerCandidates Candidates(AppDbContext db) =>
        new(db, new PriceRecorder(db), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerCandidates>.Instance);

    private Task<List<MatchCandidate>> ProposalsAsync(Guid product, Store? store = null) =>
        _factory.WithDb(db => db.MatchCandidates.AsNoTracking()
            .Where(c => c.ProductId == product && (store == null || c.Store == store) && c.Status == CandidateStatus.Proposed)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Url).ToListAsync());

    private Task<List<StoreListing>> ListingsAsync(Guid product) =>
        _factory.WithDb(async db => (await db.StoreListings.AsNoTracking().Where(l => l.ProductId == product).ToListAsync()).OrderBy(l => l.Store).ToList());

    // ---- what is proposed --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_product_gets_proposals_from_every_shops_catalog_by_size_and_name()
    {
        var owner = await CleanAsync();
        await RecordAsync(
            Reading(Store.Tesco, "254300011", "Fresh Whole Milk", "2 L"),
            Reading(Store.Tesco, "254300012", "Whole Milk", "1 L"),
            Reading(Store.Tesco, "254300013", "Whole Milk 6 x 2 L"),
            Reading(Store.Tesco, "254300014", "Skimmed Milk", "2 L"));
        await RecordAsync(Reading(Store.Aldi, "000000000000900101", "Whole Milk", "2 litres"));

        var product = await AddAsync(owner, Milk);

        var tesco = await ProposalsAsync(product, Store.Tesco);
        tesco.Should().ContainSingle().Which.Should().Match<MatchCandidate>(c =>
            c.Url == Url(Store.Tesco, "254300011") && c.Basis == MatchBasis.NameAndSize && c.Title == "Fresh Whole Milk");
        (await ProposalsAsync(product, Store.Aldi)).Should().ContainSingle().Which.Score.Should().Be(90);
        (await ProposalsAsync(product, Store.Lidl)).Should().BeEmpty();
        (await ListingsAsync(product)).Should().BeEmpty("a name match is only ever a proposal");
    }

    [Fact]
    public async Task A_six_pack_is_not_proposed_for_a_single_bottle_nor_the_other_way_round()
    {
        var owner = await CleanAsync();
        await RecordAsync(
            Reading(Store.Tesco, "254300021", "Cola", "330 ml"),
            Reading(Store.Tesco, "254300022", "Cola", "6 x 330ml"),
            Reading(Store.Tesco, "254300023", "Cola", "1.98 L"));

        var single = await AddAsync(owner, "Cola 330ml");
        var six = await AddAsync(owner, "Cola 6 x 330ml");

        (await ProposalsAsync(single)).Select(c => c.Url).Should().Equal(Url(Store.Tesco, "254300021"));
        (await ProposalsAsync(six)).Select(c => c.Url).Should().Equal(Url(Store.Tesco, "254300022"));
    }

    [Fact]
    public async Task Another_shops_own_label_is_proposed_below_a_name_match_and_a_manufacturers_brand_is_not_swapped()
    {
        var owner = await CleanAsync();
        await RecordAsync(
            Reading(Store.Lidl, "10018800", "Whole Milk", "2 L", brand: "Milbona"),
            Reading(Store.Lidl, "10018801", "Baked Beans", "400 g", brand: "Branston"),
            Reading(Store.Lidl, "10018802", "Baked Beans", "400 g", brand: "Heinz"));

        var milk = await AddAsync(owner, "Tesco whole milk 2L");
        var beans = await AddAsync(owner, "Heinz baked beans 400g");

        (await ProposalsAsync(milk)).Should().ContainSingle().Which.Should().Match<MatchCandidate>(c =>
            c.Basis == MatchBasis.Generic && c.Title == "Milbona Whole Milk");
        (await ProposalsAsync(beans)).Should().ContainSingle().Which.Title.Should().Be("Heinz Baked Beans", "the brand is part of the name the shop keeps in a field of its own");
    }

    [Fact]
    public async Task A_product_with_no_barcode_and_no_size_is_left_to_the_search_endpoint()
    {
        var owner = await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300031", "Bananas", "1 kg"), Reading(Store.Tesco, "254300032", "Bananas"));

        var product = await AddAsync(owner, "Bananas");

        (await ProposalsAsync(product)).Should().BeEmpty();
    }

    [Fact]
    public async Task Category_disagreement_drops_a_loose_match_and_agreement_ranks_higher()
    {
        var owner = await CleanAsync();
        await RecordAsync(
            Reading(Store.Lidl, "10018810", "Whole Milk", "2 L", brand: "Milbona", category: "Chilled Food > Milk"),
            Reading(Store.Lidl, "10018811", "Whole Milk Soap", "2 L", brand: "Milbona", category: "Household > Cleaning"));
        var product = await AddAsync(owner, "Tesco whole milk 2L");
        // The shelf product learns a category once a page of it is confirmed; both rows share the name and size.
        await RecordAsync(Reading(Store.Tesco, "254300041", "Whole Milk", "2 L", category: "Fresh Food > Dairy > Milk"));
        await _factory.WithDb(async db =>
        {
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = product, Store = Store.Tesco, Url = Url(Store.Tesco, "254300041"), AddedAt = DateTimeOffset.UtcNow, ConfirmedAt = DateTimeOffset.UtcNow, Category = "Fresh Food > Dairy > Milk" });
            await db.SaveChangesAsync();
            return 0;
        });

        await RunForStoreAsync(Store.Lidl);

        var lidl = await ProposalsAsync(product, Store.Lidl);
        lidl.Should().ContainSingle().Which.Url.Should().Be(Url(Store.Lidl, "10018810"));
        lidl[0].Score.Should().Be(45 + CandidateMatcher.CategoryBonus);
    }

    // ---- what is linked ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exact_barcode_links_the_page_unconfirmed_with_its_price_and_proposes_nothing_for_it()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000011");
        await RecordAsync(
            Reading(Store.Aldi, "000000000000900201", "Other words entirely", "9 pack", barcode: code, price: 2.35m, category: "Dairy > Milk"),
            Reading(Store.Aldi, "000000000000900202", "Whole Milk", "2 L"));

        var product = await AddAsync(owner, Milk, code);

        var listing = (await ListingsAsync(product)).Should().ContainSingle().Subject;
        listing.Store.Should().Be(Store.Aldi);
        listing.Url.Should().Be(Url(Store.Aldi, "000000000000900201"));
        listing.ConfirmedAt.Should().BeNull("only a person confirms a match");
        listing.StoreProductId.Should().NotBeNull();
        listing.Category.Should().Be("Dairy > Milk");
        var observation = (await _factory.WithDb(db => db.PriceObservations.Where(o => o.ListingId == listing.Id).ToListAsync())).Should().ContainSingle().Subject;
        observation.Price.Should().Be(2.35m);
        // The barcode page is the shop's page now; the other row is a name proposal beside it.
        (await ProposalsAsync(product, Store.Aldi)).Select(c => c.Url).Should().Equal(Url(Store.Aldi, "000000000000900202"));
    }

    [Fact]
    public async Task A_different_barcode_is_never_proposed_however_alike_the_names()
    {
        var owner = await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300051", "Whole Milk", "2 L", barcode: Ean13("590000000028")));

        var product = await AddAsync(owner, Milk, Ean13("590000000011"));

        (await ProposalsAsync(product)).Should().BeEmpty();
        (await ListingsAsync(product)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_already_in_the_shop_is_never_replaced_by_a_barcode_match_it_is_proposed_beside_it()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000011");
        await RecordAsync(Reading(Store.Aldi, "000000000000900211", "Whole Milk", "2 L", barcode: code));
        var product = await AddAsync(owner, "Skim milk 1L");
        var old = Url(Store.Aldi, "000000000000900299");
        await _factory.WithDb(async db =>
        {
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = product, Store = Store.Aldi, Url = old, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        (await owner.PutAsJsonAsync($"/api/products/{product}/barcode", new { barcode = code })).EnsureSuccessStatusCode();

        await RunForStoreAsync(Store.Aldi);

        (await ListingsAsync(product)).Should().ContainSingle().Which.Url.Should().Be(old);
        (await ProposalsAsync(product, Store.Aldi)).Should().ContainSingle().Which.Should().Match<MatchCandidate>(c =>
            c.Basis == MatchBasis.Barcode && c.Score == 100 && c.Url == Url(Store.Aldi, "000000000000900211"));
    }

    // ---- what is honoured --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_confirmed_shop_is_left_alone_and_the_others_still_get_proposals()
    {
        var owner = await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300061", "Whole Milk", "2 L"), Reading(Store.Tesco, "254300062", "Fresh Whole Milk", "2 L"));
        await RecordAsync(Reading(Store.Aldi, "000000000000900301", "Whole Milk", "2 L"));
        var product = await AddAsync(owner, Milk);
        var chosen = (await ProposalsAsync(product, Store.Tesco)).First();
        (await owner.PostAsync($"/api/products/{product}/candidates/{chosen.Id}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RecordAsync(Reading(Store.Tesco, "254300063", "Whole Milk Fresh", "2 L"));

        await RunForStoreAsync(Store.Tesco);

        (await ProposalsAsync(product, Store.Tesco)).Should().BeEmpty("the owner chose Tesco's page");
        (await ListingsAsync(product)).Single(l => l.Store == Store.Tesco).Url.Should().Be(chosen.Url);
        (await ProposalsAsync(product, Store.Aldi)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_rejected_page_is_never_proposed_again_by_a_later_sweep()
    {
        var owner = await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300071", "Whole Milk", "2 L"), Reading(Store.Tesco, "254300072", "Fresh Whole Milk", "2 L"));
        var product = await AddAsync(owner, Milk);
        var first = (await ProposalsAsync(product, Store.Tesco)).First();
        (await owner.PostAsync($"/api/products/{product}/candidates/{first.Id}/reject", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await RunForStoreAsync(Store.Tesco);

        (await ProposalsAsync(product, Store.Tesco)).Select(c => c.Url).Should().NotContain(first.Url).And.HaveCount(1);
        (await _factory.WithDb(db => db.MatchCandidates.CountAsync(c => c.Id == first.Id && c.Status == CandidateStatus.Rejected))).Should().Be(1);
    }

    [Fact]
    public async Task A_rejected_barcode_page_is_not_linked_either()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000011");
        await RecordAsync(Reading(Store.Aldi, "000000000000900401", "Whole Milk", "2 L", barcode: code));
        var product = await AddAsync(owner, Milk);
        var proposal = (await ProposalsAsync(product, Store.Aldi)).Should().ContainSingle().Subject;
        (await owner.PostAsync($"/api/products/{product}/candidates/{proposal.Id}/reject", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync($"/api/products/{product}/barcode", new { barcode = code })).EnsureSuccessStatusCode();

        await RunForStoreAsync(Store.Aldi);

        (await ListingsAsync(product)).Should().BeEmpty();
        (await ProposalsAsync(product, Store.Aldi)).Should().BeEmpty();
    }

    [Fact]
    public async Task At_most_five_proposals_stay_open_per_shop_and_the_strongest_are_kept()
    {
        var owner = await CleanAsync();
        // Seven rows state the size; the closer the name, the higher the score.
        var names = new[] { "Whole Milk", "Fresh Whole Milk", "Irish Fresh Whole Milk", "Irish Fresh Farm Whole Milk", "Irish Fresh Farm Dairy Whole Milk", "Very Irish Fresh Farm Dairy Whole Milk", "The Very Irish Fresh Farm Dairy Whole Milk Selection" };
        await RecordAsync(names.Select((n, i) => Reading(Store.Tesco, $"2543001{i:00}", n, "2 L")).ToArray());

        var product = await AddAsync(owner, Milk);

        var open = await ProposalsAsync(product, Store.Tesco);
        open.Should().HaveCount(MatchEndpoints.MaxProposalsPerStore);
        open.Select(c => c.Title).Should().Equal(names.Take(MatchEndpoints.MaxProposalsPerStore));
        open.Select(c => c.Score).Should().BeInDescendingOrder();

        // A second run changes nothing: same rows, same ids.
        var ids = open.Select(c => c.Id).ToList();
        await RunForStoreAsync(Store.Tesco);
        (await ProposalsAsync(product, Store.Tesco)).Select(c => c.Id).Should().Equal(ids);
    }

    [Fact]
    public async Task Products_the_shops_no_longer_list_are_not_proposed()
    {
        var owner = await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300081", "Whole Milk", "2 L"));
        await _factory.WithDb(db => db.StoreProducts.ExecuteUpdateAsync(s => s.SetProperty(p => p.DiscontinuedAt, DateTimeOffset.UtcNow)));

        var product = await AddAsync(owner, Milk);

        (await ProposalsAsync(product)).Should().BeEmpty();
    }

    // ---- the three moments -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_closed_sweep_looks_for_the_equivalents_of_every_product_with_an_open_gap_in_that_shop()
    {
        var owner = await CleanAsync();
        var milk = await AddAsync(owner, Milk);
        var butter = await AddAsync(owner, "Irish butter 250g");
        var rice = await AddAsync(owner, "Basmati rice 1kg");
        await _factory.WithDb(async db =>
        {
            // The owner already chose Tesco's rice, so the sweep has nothing to look for there.
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = rice, Store = Store.Tesco, Url = Url(Store.Tesco, "254300099"), AddedAt = DateTimeOffset.UtcNow, ConfirmedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        (await ProposalsAsync(milk)).Should().BeEmpty("the catalog was empty when the products were added");
        var started = _factory.Clock.GetUtcNow().AddMinutes(-5);

        var closed = await _factory.WithDb(async db =>
        {
            var recorder = new CatalogRecorder(db, new PriceRecorder(db));
            await recorder.RecordAsync(
                Store.Tesco,
                [
                    Reading(Store.Tesco, "254300091", "Fresh Whole Milk", "2 L"),
                    Reading(Store.Tesco, "254300092", "Irish Butter", "250 g"),
                    Reading(Store.Tesco, "254300093", "Basmati Rice", "1 kg"),
                ],
                _factory.Clock.GetUtcNow(),
                CancellationToken.None);
            await db.SaveChangesAsync();
            return await new CatalogIngest(db, recorder, _factory.Clock, Candidates(db)).CompleteAsync(Store.Tesco, 3, started, CancellationToken.None);
        });

        closed.Outcome.Should().Be("Ok");
        (await ProposalsAsync(milk, Store.Tesco)).Should().ContainSingle().Which.Url.Should().Be(Url(Store.Tesco, "254300091"));
        (await ProposalsAsync(butter, Store.Tesco)).Should().ContainSingle().Which.Url.Should().Be(Url(Store.Tesco, "254300092"));
        (await ProposalsAsync(rice, Store.Tesco)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_that_listed_nothing_runs_nothing()
    {
        var owner = await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300095", "Fresh Whole Milk", "2 L"));
        var product = await AddAsync(owner, Milk);
        await _factory.WithDb(db => db.MatchCandidates.ExecuteDeleteAsync());

        // Started after everything the catalog knows was last seen: it touched no product.
        var closed = await _factory.WithDb(async db =>
        {
            var recorder = new CatalogRecorder(db, new PriceRecorder(db));
            return await new CatalogIngest(db, recorder, _factory.Clock, Candidates(db)).CompleteAsync(Store.Tesco, 0, _factory.Clock.GetUtcNow().AddMinutes(1), CancellationToken.None);
        });

        closed.Outcome.Should().Be("Failed");
        (await ProposalsAsync(product)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_covers_more_products_than_one_batch()
    {
        var owner = await CleanAsync();
        var count = ServerCandidates.BatchSize + 5;
        var products = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            products.Add(await AddAsync(owner, $"Flour type{i} 1kg"));
        }

        await RecordAsync(Enumerable.Range(0, count).Select(i => Reading(Store.Tesco, $"2543{i:00000}", $"Flour type{i}", "1 kg")).ToArray());
        var result = await RunForStoreAsync(Store.Tesco);

        result.Should().Be(new ServerCandidateResult(count, count, 0));
        foreach (var (product, i) in products.Select((p, i) => (p, i)))
        {
            (await ProposalsAsync(product, Store.Tesco)).Should().ContainSingle().Which.Title.Should().Be($"Flour type{i}");
        }
    }

    [Fact]
    public async Task Confirming_a_page_teaches_the_product_its_barcode_and_the_other_shops_are_searched_again()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000011");
        await RecordAsync(Reading(Store.Lidl, "10018850", "Fresh Milk", "2 L", brand: "Milbona", barcode: code));
        await RecordAsync(Reading(Store.Dunnes, "100317590", "Totally Different Words", barcode: code, price: 1.19m));
        var product = await AddAsync(owner, "Milk");
        (await ListingsAsync(product)).Should().BeEmpty("the product knows neither a barcode nor a size yet");

        var response = await owner.PutAsJsonAsync($"/api/products/{product}/stores/lidl", new { url = Url(Store.Lidl, "10018850") });
        response.EnsureSuccessStatusCode();

        var listings = await ListingsAsync(product);
        listings.Select(l => l.Store).Should().Equal(Store.Lidl, Store.Dunnes);
        listings[0].ConfirmedAt.Should().NotBeNull("the person pasted this one");
        listings[1].Should().Match<StoreListing>(l => l.ConfirmedAt == null && l.Url == Url(Store.Dunnes, "100317590") && l.StoreProductId != null);
    }

    [Fact]
    public async Task Accepting_a_proposal_searches_the_other_shops_with_what_the_page_taught_the_product()
    {
        var owner = await CleanAsync();
        var code = Ean13("590000000011");
        await RecordAsync(Reading(Store.Lidl, "10018860", "Whole Milk", "2 L", barcode: code));
        await RecordAsync(Reading(Store.Aldi, "000000000000900501", "Mystery Dairy Drink", "Pack", barcode: code));
        var product = await AddAsync(owner, Milk);
        var proposal = (await ProposalsAsync(product, Store.Lidl)).Should().ContainSingle().Subject;
        (await ListingsAsync(product)).Should().BeEmpty();

        (await owner.PostAsync($"/api/products/{product}/candidates/{proposal.Id}/confirm", null)).EnsureSuccessStatusCode();

        (await ListingsAsync(product)).Select(l => (l.Store, Confirmed: l.ConfirmedAt != null)).Should().Equal((Store.Lidl, true), (Store.Aldi, false));
    }

    [Fact]
    public async Task The_fleet_adding_a_product_runs_the_same_search()
    {
        await CleanAsync();
        await RecordAsync(Reading(Store.Tesco, "254300111", "Fresh Whole Milk", "2 L"));
        var fleet = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        fleet.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Fleet.Mint("fleet:read basket:catalog-add"));

        var product = await AddAsync(fleet, Milk);

        (await ProposalsAsync(product, Store.Tesco)).Should().ContainSingle();
    }
}
