using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Matching;
using Basket.Api.Prices;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// Candidate matching through the real endpoints: proposals are stored apart from confirmed listings, a match a person
/// confirmed is never overwritten, and the fleet identity may propose with its catalog scopes and nothing more.
/// </summary>
public sealed class MatchingTests : IClassFixture<BasketFactory>
{
    private const string Milk = "Whole milk 2L";
    private const string LidlMilk = "https://www.lidl.ie/p/milbona-fresh-whole-milk-2-l/p10018800";
    private const string LidlMilkOther = "https://www.lidl.ie/p/milbona-fresh-whole-milk-2-l/p10018801";

    private readonly BasketFactory _factory;

    public MatchingTests(BasketFactory factory) => _factory = factory;

    /// <summary>An EAN-13 for the given 12 digits, with its check digit appended.</summary>
    private static string Ean13(string twelve)
    {
        var sum = twelve.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + ((10 - (sum % 10)) % 10);
    }

    private HttpClient Fleet(params string[] scopes)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        if (scopes.Length > 0)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Fleet.Mint(string.Join(' ', scopes)));
        }

        return client;
    }

    private async Task<(HttpClient Owner, Guid ProductId)> WithProductAsync(string name = Milk, string? barcode = null)
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(db => db.Products.ExecuteDeleteAsync());
        var response = await owner.PostAsJsonAsync("/api/products", new { name, barcode });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (owner, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static object Result(string url, string? title, string? barcode = null) => new { url, title, barcode };

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Outcomes(JsonElement body) =>
        body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("outcome").GetString()!).ToArray();

    // ---- proposing ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_results_become_proposals_ranked_by_how_sure_the_match_is_and_the_rest_are_named_with_a_reason()
    {
        var code = Ean13("500011263792");
        var (owner, id) = await WithProductAsync(Milk, code);
        var fleet = Fleet(FleetScopes.CatalogAdd);

        var response = await fleet.PostAsJsonAsync($"/api/products/{id}/candidates", new
        {
            store = "lidl",
            results = new[]
            {
                Result(LidlMilk, "Milbona fresh whole milk 2 litres"),
                Result("https://www.lidl.ie/p/milbona-skimmed-milk-1-l/p10018802", "Milbona skimmed milk 1 l"),
                Result("https://www.lidl.ie/p/other-name/p10018803", "Something else entirely 2L", code),
                Result("https://www.lidl.ie/not-a-product", "Whole milk 2L"),
                Result(LidlMilk + "?utm=x", "Milbona fresh whole milk 2 litres"),
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        Outcomes(body).Should().Equal("proposed", "no_match", "proposed", "url_invalid", "duplicate");
        body.GetProperty("proposed").GetInt32().Should().Be(2);

        var list = await Json(await owner.GetAsync($"/api/products/{id}/candidates"));
        var candidates = list.GetProperty("candidates").EnumerateArray().ToList();
        candidates.Select(c => c.GetProperty("basis").GetString()).Should().Equal("barcode", "name_size");
        candidates[0].GetProperty("score").GetInt32().Should().Be(100);
        candidates[0].GetProperty("barcode").GetString().Should().Be("0" + code);
        candidates[1].GetProperty("url").GetString().Should().Be(LidlMilk);
        candidates[1].GetProperty("score").GetInt32().Should().Be(70); // two extra words (brand, fresh) cost 20 of the 40
    }

    [Fact]
    public async Task Proposals_are_stored_apart_from_listings_and_change_nothing_a_person_confirmed()
    {
        var (owner, id) = await WithProductAsync();
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/aldi", new { url = "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541" })).EnsureSuccessStatusCode();
        var before = await _factory.WithDb(db => db.StoreListings.AsNoTracking().SingleAsync(l => l.ProductId == id));

        (await owner.PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } })).EnsureSuccessStatusCode();

        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.ProductId == id))).Should().Be(1);
        var after = await _factory.WithDb(db => db.StoreListings.AsNoTracking().SingleAsync(l => l.ProductId == id));
        after.Should().BeEquivalentTo(before);
        (await _factory.WithDb(db => db.MatchCandidates.CountAsync(c => c.ProductId == id))).Should().Be(1);
    }

    [Fact]
    public async Task A_shop_with_a_confirmed_page_gets_no_proposals()
    {
        var (owner, id) = await WithProductAsync();
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilk })).EnsureSuccessStatusCode();

        var response = await Fleet(FleetScopes.CatalogAdd).PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilkOther, Milk) } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(response)).GetProperty("error").GetString().Should().Be("match_confirmed");
        (await _factory.WithDb(db => db.MatchCandidates.CountAsync(c => c.ProductId == id))).Should().Be(0);

        // Other shops are unaffected.
        (await Fleet(FleetScopes.CatalogAdd).PostAsJsonAsync($"/api/products/{id}/candidates",
            new { store = "aldi", results = new[] { Result("https://www.aldi.ie/product/clonbawn-whole-milk-000000000000422542", Milk) } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_page_a_person_rejected_is_not_proposed_again()
    {
        var (_, id) = await WithProductAsync();
        var fleet = Fleet(FleetScopes.CatalogAdd);
        (await fleet.PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } })).EnsureSuccessStatusCode();
        await _factory.WithDb(db => db.MatchCandidates.Where(c => c.ProductId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CandidateStatus.Rejected)));

        var again = await Json(await fleet.PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } }));

        Outcomes(again).Should().Equal("rejected");
        (await _factory.WithDb(db => db.MatchCandidates.SingleAsync(c => c.ProductId == id))).Status.Should().Be(CandidateStatus.Rejected);
    }

    [Fact]
    public async Task Posting_the_same_page_again_updates_its_proposal_instead_of_adding_one()
    {
        var (_, id) = await WithProductAsync();
        var fleet = Fleet(FleetScopes.CatalogAdd);
        await fleet.PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, "Milbona fresh whole milk 2 litres") } });

        await fleet.PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, "Whole milk 2L") } });

        var row = await _factory.WithDb(db => db.MatchCandidates.SingleAsync(c => c.ProductId == id));
        row.Title.Should().Be("Whole milk 2L");
        row.Score.Should().Be(90);
    }

    [Fact]
    public async Task Only_the_best_few_proposals_per_shop_stay_open()
    {
        var code = Ean13("500011263792");
        var (_, id) = await WithProductAsync(Milk, code);
        await _factory.WithDb(async db =>
        {
            for (var i = 0; i < MatchEndpoints.MaxProposalsPerStore; i++)
            {
                db.MatchCandidates.Add(new MatchCandidate
                {
                    Id = Guid.NewGuid(), ProductId = id, Store = Store.Lidl, Url = $"https://www.lidl.ie/p/milk-{i}/p1001880{i}",
                    Title = "Whole milk 2L", Basis = MatchBasis.NameAndSize, Score = 70 + i, Status = CandidateStatus.Proposed, ProposedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var body = await Json(await Fleet(FleetScopes.CatalogAdd).PostAsJsonAsync($"/api/products/{id}/candidates",
            new { store = "lidl", results = new[] { Result(LidlMilk, "Anything", code) } }));

        Outcomes(body).Should().Equal("proposed");
        var open = await _factory.WithDb(db => db.MatchCandidates.Where(c => c.ProductId == id).OrderByDescending(c => c.Score).ToListAsync());
        open.Should().HaveCount(MatchEndpoints.MaxProposalsPerStore);
        open[0].Url.Should().Be(LidlMilk);
        open.Select(c => c.Score).Should().NotContain(70);
    }

    [Theory]
    [InlineData("""{"store":"sainsburys","results":[{"url":"https://x"}]}""")]
    [InlineData("""{"store":"lidl","results":[]}""")]
    [InlineData("""{"store":"lidl"}""")]
    [InlineData("""{"results":[{"url":"https://x"}]}""")]
    public async Task A_malformed_post_is_refused(string json)
    {
        var (owner, id) = await WithProductAsync();

        var response = await owner.PostAsync($"/api/products/{id}/candidates", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        var (owner, _) = await _factory.OwnerAsync();

        (await owner.PostAsJsonAsync($"/api/products/{Guid.NewGuid()}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync($"/api/products/{Guid.NewGuid()}/candidates")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- a confirmed match is not overwritten -------------------------------------------------------------------

    [Fact]
    public async Task A_person_pasting_a_page_confirms_it_and_settles_the_open_proposals()
    {
        var (owner, id) = await WithProductAsync();
        await Fleet(FleetScopes.CatalogAdd).PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } });

        var linked = await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilkOther });

        (await Json(linked)).GetProperty("confirmed").GetBoolean().Should().BeTrue();
        (await _factory.WithDb(db => db.MatchCandidates.CountAsync(c => c.ProductId == id))).Should().Be(0);
        var all = await Json(await owner.GetAsync("/api/products"));
        var store = all.GetProperty("products")[0].GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "lidl");
        store.GetProperty("listing").GetProperty("confirmed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_fleet_cannot_replace_a_confirmed_page_but_may_repeat_it()
    {
        var (owner, id) = await WithProductAsync();
        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilk })).EnsureSuccessStatusCode();
        var fleet = Fleet(FleetScopes.CatalogAdd);

        var swap = await fleet.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilkOther });
        var same = await fleet.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilk });

        swap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(swap)).GetProperty("error").GetString().Should().Be("match_confirmed");
        same.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == id));
        stored.Url.Should().Be(LidlMilk);
        stored.ConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_page_the_fleet_linked_is_not_confirmed_and_a_person_can_still_choose_another()
    {
        var (owner, id) = await WithProductAsync();
        (await Fleet(FleetScopes.CatalogAdd).PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilk })).EnsureSuccessStatusCode();
        (await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == id))).ConfirmedAt.Should().BeNull();

        // Proposals are still allowed beside it, except for the page it already points at.
        var body = await Json(await Fleet(FleetScopes.CatalogAdd).PostAsJsonAsync($"/api/products/{id}/candidates",
            new { store = "lidl", results = new[] { Result(LidlMilk, Milk), Result(LidlMilkOther, Milk) } }));
        Outcomes(body).Should().Equal("already_linked", "proposed");

        (await owner.PutAsJsonAsync($"/api/products/{id}/stores/lidl", new { url = LidlMilkOther })).EnsureSuccessStatusCode();
        (await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == id))).ConfirmedAt.Should().NotBeNull();
    }

    // ---- barcodes -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_product_can_be_added_with_a_barcode_and_it_is_kept_as_a_14_digit_gtin()
    {
        var code = Ean13("500011263792");
        var (owner, id) = await WithProductAsync(Milk, code);

        var all = await Json(await owner.GetAsync("/api/products"));

        all.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id).GetProperty("barcode").GetString().Should().Be("0" + code);
    }

    [Fact]
    public async Task A_barcode_is_checked_and_belongs_to_one_product()
    {
        var code = Ean13("500011263792");
        var (owner, id) = await WithProductAsync(Milk, code);

        (await owner.PostAsJsonAsync("/api/products", new { name = "Other", barcode = "1234567890123" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/products", new { name = "Other", barcode = code })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var other = await Json(await owner.PostAsJsonAsync("/api/products", new { name = "Other" }));
        var otherId = other.GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/products/{otherId}/barcode", new { barcode = code })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.PutAsJsonAsync($"/api/products/{otherId}/barcode", new { barcode = "nope" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync($"/api/products/{id}/barcode", new { barcode = code })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_barcode_can_be_set_and_cleared_by_the_fleet()
    {
        var code = Ean13("500011263792");
        var (_, id) = await WithProductAsync();
        var fleet = Fleet(FleetScopes.CatalogAdd);

        (await Json(await fleet.PutAsJsonAsync($"/api/products/{id}/barcode", new { barcode = code }))).GetProperty("barcode").GetString().Should().Be("0" + code);
        (await _factory.WithDb(db => db.Products.SingleAsync(p => p.Id == id))).Barcode.Should().Be("0" + code);

        (await fleet.PutAsJsonAsync($"/api/products/{id}/barcode", new { barcode = "" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.WithDb(db => db.Products.SingleAsync(p => p.Id == id))).Barcode.Should().BeNull();
    }

    // ---- who may call it ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_fleet_needs_the_right_scope_and_no_other_token_will_do()
    {
        var (_, id) = await WithProductAsync();
        var post = new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } };

        foreach (var wrong in new[] { FleetScopes.CatalogRead, FleetScopes.PriceIngest })
        {
            (await Fleet(wrong).PostAsJsonAsync($"/api/products/{id}/candidates", post)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Fleet(wrong).PutAsJsonAsync($"/api/products/{id}/barcode", new { barcode = "" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        foreach (var wrong in new[] { FleetScopes.CatalogAdd, FleetScopes.PriceIngest })
        {
            (await Fleet(wrong).GetAsync($"/api/products/{id}/candidates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // No token and no Origin: the origin check may answer first (403), as for every other fleet route.
        ((int)(await Fleet().PostAsJsonAsync($"/api/products/{id}/candidates", post)).StatusCode).Should().BeOneOf(401, 403);
        (await Fleet(FleetScopes.CatalogRead).GetAsync($"/api/products/{id}/candidates")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.WithDb(db => db.MatchCandidates.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_browser_session_without_the_origin_header_cannot_post_proposals()
    {
        var (_, id) = await WithProductAsync();
        var (_, passkey) = await _factory.OwnerAsync();
        var bare = _factory.NewBrowser();
        (await _factory.SignInAsync(bare, passkey)).EnsureSuccessStatusCode();
        bare.DefaultRequestHeaders.Remove("Origin");

        var response = await bare.PostAsJsonAsync($"/api/products/{id}/candidates", new { store = "lidl", results = new[] { Result(LidlMilk, Milk) } });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
