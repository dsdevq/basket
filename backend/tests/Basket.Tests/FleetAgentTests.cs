using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.Prices;
using Basket.Api.Prices.Ingest;
using Basket.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Basket.Tests;

/// <summary>
/// The fleet's machine identity: accepted on the price ingest and the product catalog (read, add) with the right scope,
/// refused everywhere else. All tokens here are signed by a key generated for the run; nothing real is used.
/// </summary>
public sealed class FleetAgentTests : IClassFixture<BasketFactory>
{
    private const string TescoUrl = "https://www.tesco.ie/groceries/en-IE/products/254317390";

    private readonly BasketFactory _factory;

    public FleetAgentTests(BasketFactory factory) => _factory = factory;

    private async Task<HttpClient> SeedAsync()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await _factory.WithDb(async db =>
        {
            await db.Products.ExecuteDeleteAsync();
            await db.PricePullRuns.ExecuteDeleteAsync();
            await db.IngestTokens.ExecuteDeleteAsync();
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            db.Products.Add(product);
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Tesco, Url = TescoUrl, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        return owner;
    }

    /// <summary>The fleet: a plain HTTP client with no cookie and no Origin header, only the bearer token.</summary>
    private static HttpClient Fleet(BasketFactory factory, string? token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BasketFactory.Origin), HandleCookies = false, AllowAutoRedirect = false });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private HttpClient Fleet(string? token) => Fleet(_factory, token);

    private string Token(params string[] scopes) => _factory.Fleet.Mint(string.Join(' ', scopes));

    private static Task<HttpResponseMessage> PostPricesAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/ingest/prices", new { prices = new[] { new { store = "tesco", url = TescoUrl, price = 2.15m } } });

    // ---- price ingest -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fleet_token_with_the_ingest_scope_posts_prices_like_the_reader_does()
    {
        await SeedAsync();

        var response = await PostPricesAsync(Fleet(Token(FleetScopes.PriceIngest)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32().Should().Be(1);
        (await _factory.WithDb(db => db.PriceObservations.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task The_ingest_scope_is_read_from_a_space_separated_list_with_the_others()
    {
        await SeedAsync();

        var response = await PostPricesAsync(Fleet(Token("fleet:read", FleetScopes.PriceIngest, FleetScopes.CatalogRead)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(FleetScopes.CatalogRead)]
    [InlineData(FleetScopes.CatalogAdd)]
    [InlineData("fleet:read")]
    [InlineData("basket:price-ingest-all")]
    public async Task A_fleet_token_without_the_ingest_scope_is_forbidden_and_writes_nothing(string scope)
    {
        await SeedAsync();

        var response = await PostPricesAsync(Fleet(Token(scope)));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.WithDb(db => db.PriceObservations.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_owner_minted_bsk_token_still_works_with_the_fleet_path_on()
    {
        var owner = await SeedAsync();
        var created = await owner.PostAsJsonAsync("/api/ingest-tokens", new { label = "Home PC" });
        var raw = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var good = await PostPricesAsync(Fleet(raw));
        var bad = await PostPricesAsync(Fleet("bsk_" + new string('A', 43)));

        good.StatusCode.Should().Be(HttpStatusCode.OK);
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        bad.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
    }

    // ---- tokens that are not good -------------------------------------------------------------------------------

    public static TheoryData<string> BadTokens => new()
    {
        "expired",
        "wrong_audience",
        "wrong_issuer",
        "forged_signature",
        "garbage",
        "unsigned",
    };

    private string BadToken(string kind) => kind switch
    {
        "expired" => _factory.Fleet.Mint(FleetScopes.PriceIngest + " " + FleetScopes.CatalogRead + " " + FleetScopes.CatalogAdd, expiresIn: TimeSpan.FromHours(-1)),
        "wrong_audience" => _factory.Fleet.Mint(FleetScopes.PriceIngest + " " + FleetScopes.CatalogRead + " " + FleetScopes.CatalogAdd, audience: "https://someone-else.test/api"),
        "wrong_issuer" => _factory.Fleet.Mint(FleetScopes.PriceIngest + " " + FleetScopes.CatalogRead + " " + FleetScopes.CatalogAdd, issuer: "https://elsewhere.test/oidc"),
        "forged_signature" => _factory.Fleet.MintForged(FleetScopes.PriceIngest + " " + FleetScopes.CatalogRead + " " + FleetScopes.CatalogAdd),
        "unsigned" => Unsigned(),
        _ => "not.a.jwt",
    };

    private static string Unsigned()
    {
        static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64($"{{\"iss\":\"{FleetTokens.Issuer}\",\"aud\":\"{FleetTokens.Audience}\",\"exp\":{exp},\"scope\":\"basket:price-ingest basket:catalog-read basket:catalog-add\"}}")}.";
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task An_expired_misaddressed_forged_or_unsigned_token_is_refused_on_every_machine_endpoint(string kind)
    {
        await SeedAsync();
        var client = Fleet(BadToken(kind));

        var ingest = await PostPricesAsync(client);
        var read = await client.GetAsync("/api/products");
        var add = await client.PostAsJsonAsync("/api/products", new { name = "Bread" });

        ingest.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        add.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.WithDb(db => db.PriceObservations.CountAsync())).Should().Be(0);
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_disabled_application_gets_a_token_with_no_scope_and_every_machine_endpoint_refuses_it(string? scope)
    {
        // Logto has no per-application switch: revoking takes the role away, so the token still comes, scope-less.
        await SeedAsync();
        var client = Fleet(_factory.Fleet.Mint(scope));

        (await PostPricesAsync(client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/products", new { name = "Bread" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(1);
    }

    // ---- product catalog ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fleet_token_with_the_catalog_read_scope_lists_the_products()
    {
        await SeedAsync();

        var response = await Fleet(Token(FleetScopes.CatalogRead)).GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Milk");
    }

    [Theory]
    [InlineData(FleetScopes.PriceIngest)]
    [InlineData(FleetScopes.CatalogAdd)]
    public async Task Reading_the_catalog_needs_its_own_scope(string scope)
    {
        await SeedAsync();

        (await Fleet(Token(scope)).GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_fleet_token_with_the_catalog_add_scope_adds_a_product_without_an_origin_header()
    {
        var owner = await SeedAsync();

        var response = await Fleet(Token(FleetScopes.CatalogAdd)).PostAsJsonAsync("/api/products", new { name = "  Brown   bread " });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var ownerId = await _factory.WithDb(db => db.Users.Select(u => u.Id).FirstAsync());
        var stored = await _factory.WithDb(db => db.Products.SingleAsync(p => p.Id == id));
        stored.Name.Should().Be("Brown bread");
        stored.AddedByUserId.Should().Be(ownerId);

        // The person sees it like any other product.
        (await owner.GetStringAsync("/api/products")).Should().Contain("Brown bread");
    }

    [Fact]
    public async Task Adding_keeps_the_same_rules_for_the_fleet_as_for_a_person()
    {
        await SeedAsync();
        var client = Fleet(Token(FleetScopes.CatalogAdd));

        (await client.PostAsJsonAsync("/api/products", new { name = "  " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/products", new { name = "milk" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(FleetScopes.PriceIngest)]
    [InlineData(FleetScopes.CatalogRead)]
    public async Task Adding_a_product_needs_its_own_scope(string scope)
    {
        await SeedAsync();

        var response = await Fleet(Token(scope)).PostAsJsonAsync("/api/products", new { name = "Bread" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(1);
    }

    // ---- linking a store page ------------------------------------------------------------------------------------

    private static string LinkPath(Guid id, string store = "tesco") => $"/api/products/{id}/stores/{store}";

    [Fact]
    public async Task A_fleet_token_with_the_catalog_add_scope_links_a_store_page_without_an_origin_header()
    {
        var owner = await SeedAsync();
        var created = await Fleet(Token(FleetScopes.CatalogAdd)).PostAsJsonAsync("/api/products", new { name = "Bread" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await Fleet(Token(FleetScopes.CatalogAdd)).PutAsJsonAsync(LinkPath(id), new { url = TescoUrl + "?utm=x" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.WithDb(db => db.StoreListings.SingleAsync(l => l.ProductId == id));
        stored.Store.Should().Be(Store.Tesco);
        stored.Url.Should().Be(TescoUrl);
        (await owner.GetStringAsync("/api/products")).Should().Contain(TescoUrl);
    }

    [Fact]
    public async Task Linking_keeps_the_same_checks_for_the_fleet_as_for_a_person()
    {
        await SeedAsync();
        var id = await _factory.WithDb(db => db.Products.Select(p => p.Id).FirstAsync());
        var client = Fleet(Token(FleetScopes.CatalogAdd));

        (await client.PutAsJsonAsync(LinkPath(id), new { url = "https://example.com/x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(LinkPath(id, "supervalu"), new { url = TescoUrl })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(LinkPath(Guid.NewGuid()), new { url = TescoUrl })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(FleetScopes.PriceIngest)]
    [InlineData(FleetScopes.CatalogRead)]
    public async Task Linking_a_store_page_needs_the_catalog_add_scope(string scope)
    {
        await SeedAsync();
        var id = await _factory.WithDb(db => db.Products.Select(p => p.Id).FirstAsync());

        var response = await Fleet(Token(scope)).PutAsJsonAsync(LinkPath(id, "aldi"), new { url = "https://www.aldi.ie/product/actileaf-uht-coconut-milk-000000000000423109" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.Store == Store.Aldi))).Should().Be(0);
    }

    [Fact]
    public async Task A_fleet_token_cannot_delete_a_product_or_unlink_a_store_page()
    {
        await SeedAsync();
        var id = await _factory.WithDb(db => db.Products.Select(p => p.Id).FirstAsync());
        var client = Fleet(Token(FleetScopes.PriceIngest, FleetScopes.CatalogRead, FleetScopes.CatalogAdd));

        foreach (var path in new[] { $"/api/products/{id}", LinkPath(id) })
        {
            ((int)(await client.DeleteAsync(path)).StatusCode).Should().BeOneOf(new[] { 401, 403 }, path);
            var withOrigin = new HttpRequestMessage(HttpMethod.Delete, path);
            withOrigin.Headers.Add("Origin", BasketFactory.Origin);
            ((int)(await client.SendAsync(withOrigin)).StatusCode).Should().BeOneOf(new[] { 401, 403 }, path);
        }

        (await _factory.WithDb(db => db.Products.CountAsync(p => p.Id == id))).Should().Be(1);
        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.ProductId == id))).Should().Be(1);
    }

    [Fact]
    public async Task A_bearer_link_request_cannot_be_carried_by_the_cookie()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await SeedAsync();
        var id = await _factory.WithDb(db => db.Products.Select(p => p.Id).FirstAsync());

        var request = new HttpRequestMessage(HttpMethod.Put, LinkPath(id, "aldi")) { Content = JsonContent.Create(new { url = "https://www.aldi.ie/product/actileaf-uht-coconut-milk-000000000000423109" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        var response = await owner.SendAsync(request);

        ((int)response.StatusCode).Should().BeOneOf(401, 403);
        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.Store == Store.Aldi))).Should().Be(0);
    }

    // ---- everything else stays people-only ----------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/list")]
    [InlineData("GET", "/api/promotions")]
    [InlineData("POST", "/api/list/items")]
    [InlineData("DELETE", "/api/list/items/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/products/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/products/00000000-0000-0000-0000-000000000001/stores/lidl")]
    [InlineData("GET", "/api/invites")]
    [InlineData("POST", "/api/invites")]
    [InlineData("DELETE", "/api/invites/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/members")]
    [InlineData("GET", "/api/ingest-tokens")]
    [InlineData("POST", "/api/ingest-tokens")]
    [InlineData("DELETE", "/api/ingest-tokens/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/passkeys")]
    [InlineData("POST", "/api/passkeys/options")]
    [InlineData("POST", "/api/passkeys")]
    [InlineData("DELETE", "/api/passkeys/AAAA")]
    [InlineData("POST", "/api/auth/sign-out")]
    public async Task A_fleet_token_with_every_scope_is_refused_on_every_other_endpoint(string method, string path)
    {
        await SeedAsync();
        var all = Token(FleetScopes.PriceIngest, FleetScopes.CatalogRead, FleetScopes.CatalogAdd, "fleet:read", "fleet:recategorise");

        foreach (var origin in new string?[] { null, BasketFactory.Origin })
        {
            var client = Fleet(all);
            var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }),
            };
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            var response = await client.SendAsync(request);

            ((int)response.StatusCode).Should().BeOneOf(new[] { 401, 403 }, $"{method} {path} (origin {origin ?? "none"}) is people-only");
        }
    }

    [Fact]
    public async Task A_fleet_token_does_not_sign_anyone_in()
    {
        await SeedAsync();

        var session = await Fleet(Token(FleetScopes.CatalogRead, FleetScopes.CatalogAdd, FleetScopes.PriceIngest)).GetFromJsonAsync<JsonElement>("/api/session");

        session.GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_bearer_header_makes_the_request_stand_on_the_token_alone_so_a_cookie_cannot_carry_it()
    {
        var (owner, _) = await _factory.OwnerAsync();
        await SeedAsync();

        // The signed-in owner, but the request carries a bad bearer and a foreign origin: nothing is added.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/products") { Content = JsonContent.Create(new { name = "Bread" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        request.Headers.Add("Origin", "https://evil.test");
        var withBadToken = await owner.SendAsync(request);

        // Without a bearer, a foreign origin is still refused for the cookie.
        var plain = new HttpRequestMessage(HttpMethod.Post, "/api/products") { Content = JsonContent.Create(new { name = "Bread" }) };
        plain.Headers.Add("Origin", "https://evil.test");
        var crossSite = await owner.SendAsync(plain);

        ((int)withBadToken.StatusCode).Should().BeOneOf(401, 403);
        crossSite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.WithDb(db => db.Products.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task People_use_the_catalog_exactly_as_before()
    {
        var owner = await SeedAsync();

        (await owner.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync("/api/products", new { name = "Eggs" })).StatusCode.Should().Be(HttpStatusCode.Created);
        var added = await _factory.WithDb(db => db.Products.SingleAsync(p => p.Name == "Eggs"));
        added.AddedByUserId.Should().Be(await _factory.WithDb(db => db.Users.Select(u => u.Id).FirstAsync()));
    }

    [Fact]
    public async Task A_person_still_links_a_store_page_as_before()
    {
        var owner = await SeedAsync();
        var id = await _factory.WithDb(db => db.Products.Select(p => p.Id).FirstAsync());

        var response = await owner.PutAsJsonAsync(LinkPath(id, "aldi"), new { url = "https://www.aldi.ie/product/actileaf-uht-coconut-milk-000000000000423109" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.WithDb(db => db.StoreListings.CountAsync(l => l.Store == Store.Aldi))).Should().Be(1);
    }

    // ---- the path is off until configured -----------------------------------------------------------------------

    [Fact]
    public async Task With_no_issuer_configured_no_fleet_token_is_accepted_anywhere_and_bsk_tokens_still_work()
    {
        await using var factory = new BasketFactory { FleetAgentEnabled = false };
        var (owner, _) = await factory.OwnerAsync();
        await factory.WithDb(async db =>
        {
            var product = new Product { Id = Guid.NewGuid(), Name = "Milk", AddedAt = DateTimeOffset.UtcNow, AddedByUserId = await db.Users.Select(u => u.Id).FirstAsync() };
            db.Products.Add(product);
            db.StoreListings.Add(new StoreListing { Id = Guid.NewGuid(), ProductId = product.Id, Store = Store.Tesco, Url = TescoUrl, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        var token = factory.Fleet.Mint(FleetScopes.PriceIngest + " " + FleetScopes.CatalogRead + " " + FleetScopes.CatalogAdd);
        var raw = (await (await owner.PostAsJsonAsync("/api/ingest-tokens", new { label = "Home PC" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var client = Fleet(factory, token);
        (await PostPricesAsync(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/products", new { name = "Bread" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostPricesAsync(Fleet(factory, raw))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_issuer_reported_by_the_fetched_metadata_is_not_trusted_only_the_configured_one_is()
    {
        const string metadataIssuer = "http://logto:3001/oidc";
        await using var factory = new BasketFactory { MetadataIssuer = metadataIssuer };
        await factory.OwnerAsync();
        var scopes = FleetScopes.PriceIngest + " " + FleetScopes.CatalogRead + " " + FleetScopes.CatalogAdd;

        var fromMetadata = Fleet(factory, factory.Fleet.Mint(scopes, issuer: metadataIssuer));
        (await PostPricesAsync(fromMetadata)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fromMetadata.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fromMetadata.PostAsJsonAsync("/api/products", new { name = "Bread" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var configured = Fleet(factory, factory.Fleet.Mint(scopes));
        (await configured.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void The_scopes_are_the_ones_the_identity_service_grants()
    {
        FleetScopes.PriceIngest.Should().Be("basket:price-ingest");
        FleetScopes.CatalogRead.Should().Be("basket:catalog-read");
        FleetScopes.CatalogAdd.Should().Be("basket:catalog-add");
    }
}
