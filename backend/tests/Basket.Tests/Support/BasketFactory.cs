using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Basket.Api.LidlPlus;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;

namespace Basket.Tests.Support;

/// <summary>
/// The whole app on an in-memory server over a throwaway Postgres database. Point <c>BASKET_TEST_POSTGRES</c>
/// at a server the test user may create databases on (CI and scripts/test-backend.sh both do).
/// </summary>
public class BasketFactory : WebApplicationFactory<Program>
{
    public const string Origin = "https://basket.test";
    public const string BootstrapToken = "test-bootstrap-token-0123456789abcdef0123456789";

    private readonly string _adminConnection = Environment.GetEnvironmentVariable("BASKET_TEST_POSTGRES")
        ?? throw new InvalidOperationException(
            "BASKET_TEST_POSTGRES is not set. Example: Host=localhost;Port=55432;Username=basket;Password=basket-dev;Database=postgres");

    private readonly string _database = $"basket_test_{Guid.NewGuid():N}";

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>What every test host has logged so far; see <see cref="LogCapture"/>.</summary>
    public LogCapture Logs => LogCapture.Shared;

    /// <summary>Runs on its own connection just before a new loyalty card is written, so a test can cause the moment the insert meets.</summary>
    public Func<Task>? BeforeCardInsert { get; set; }

    /// <summary>Runs on its own connection just before a new paid price is written, so a test can make another pull get there first.</summary>
    public Func<Task>? BeforePaidPriceInsert { get; set; }

    /// <summary>Lidl's sign-in service, as a test sets it up: what it answers and which tokens it was asked about.</summary>
    public FakeLidlPlusAuth Lidl { get; } = new();

    /// <summary>Lidl Plus's receipts service, as a test sets it up: which receipts exist and which calls were made.</summary>
    public FakeLidlPlusTickets Receipts { get; } = new();

    public int AuthRequestsPerMinute { get; init; } = 1000;

    /// <summary>The price and catalog ingest limit per client and minute; the app's own default unless a test needs more room.</summary>
    public int IngestRequestsPerMinute { get; init; } = 30;

    public string? Bootstrap { get; init; } = BootstrapToken;

    /// <summary>Whether the host serves /metrics. Off by default: each host's meter provider stays in memory for the whole run.</summary>
    public bool Metrics { get; init; }

    /// <summary>The fleet identity's issuer and signing key, as the app is configured to trust them. Off when false.</summary>
    public bool FleetAgentEnabled { get; init; } = true;

    /// <summary>The issuer the (test-injected) discovery document reports; defaults to the configured one.</summary>
    public string MetadataIssuer { get; init; } = FleetTokens.Issuer;

    public FleetTokens Fleet { get; } = new();

    /// <summary>The org identity provider the app is configured to sign people in through. Null = the sign-in path is off.</summary>
    public FakeIdentityProvider? IdentityProvider { get; init; }

    public BasketFactory()
    {
        using var connection = new NpgsqlConnection(_adminConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_database}\"";
        command.ExecuteNonQuery();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Fleet.Dispose();
        IdentityProvider?.Dispose();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connection = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _database, Pooling = true, MaxPoolSize = 10 };
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connection.ConnectionString);
        builder.UseSetting("Auth:Origins:0", Origin);
        builder.UseSetting("Auth:AuthRequestsPerMinute", AuthRequestsPerMinute.ToString());
        builder.UseSetting("Auth:IngestRequestsPerMinute", IngestRequestsPerMinute.ToString());
        builder.UseSetting("Hangfire:Enabled", "false");
        builder.UseSetting("Observability:Metrics:Enabled", Metrics ? "true" : "false");
        builder.UseSetting("LidlPlus:ReceiptPauseMilliseconds", "0");
        if (Bootstrap is not null)
        {
            builder.UseSetting("Auth:BootstrapToken", Bootstrap);
        }

        if (FleetAgentEnabled)
        {
            builder.UseSetting("FleetAgent:Authority", FleetTokens.Issuer);
        }

        if (IdentityProvider is not null)
        {
            builder.UseSetting("Auth:Oidc:Authority", FakeIdentityProvider.Authority);
            builder.UseSetting("Auth:Oidc:BackchannelAuthority", FakeIdentityProvider.Backchannel);
            builder.UseSetting("Auth:Oidc:ClientId", FakeIdentityProvider.ClientId);
            builder.UseSetting("Auth:Oidc:ClientSecret", FakeIdentityProvider.ClientSecret);
            builder.UseSetting("Auth:Oidc:PublicBaseUrl", Origin);
        }

        builder.ConfigureTestServices(services =>
        {
            if (IdentityProvider is { } provider)
            {
                // The container's own back channel, replaced by the in-process provider; the rewrite handler stays in front of it.
                services.Configure<OpenIdConnectOptions>(OidcLoginOptions.Scheme, options =>
                    options.BackchannelHttpHandler = new OidcAuthorityRewriteHandler(
                        FakeIdentityProvider.Authority, FakeIdentityProvider.Backchannel, provider));
            }

            services.AddSingleton<Serilog.Core.ILogEventSink>(Logs);
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(new SaveHooks(this)));
            services.RemoveAll<ILidlPlusAuth>();
            services.AddSingleton<ILidlPlusAuth>(Lidl);
            services.RemoveAll<ILidlPlusTickets>();
            services.AddSingleton<ILidlPlusTickets>(Receipts);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (FleetAgentEnabled)
            {
                // The identity service's discovery document, replaced by a fixed one holding the test key.
                var configuration = new OpenIdConnectConfiguration { Issuer = MetadataIssuer };
                configuration.SigningKeys.Add(Fleet.PublicKey);
                services.Configure<JwtBearerOptions>(FleetAgent.Scheme, options => options.Configuration = configuration);
            }
        });
    }

    /// <summary>A browser-like client: https origin, cookies kept, the Origin header a page would send.</summary>
    public HttpClient NewBrowser(string origin = Origin)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(origin),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Registers a person through the real endpoints and leaves the client signed in.</summary>
    public async Task<(FakeAuthenticator Passkey, JsonElement Session)> RegisterAsync(HttpClient client, string token, string name)
    {
        var passkey = new FakeAuthenticator(Origin);
        var options = await client.PostAsJsonAsync("/api/auth/register/options", new { token, displayName = name });
        options.EnsureSuccessStatusCode();
        var credential = passkey.Create(await options.Content.ReadAsStringAsync());
        var done = await client.PostAsJsonAsync("/api/auth/register", new { token, credentialJson = credential });
        done.EnsureSuccessStatusCode();
        return (passkey, await done.Content.ReadFromJsonAsync<JsonElement>());
    }

    public async Task<HttpResponseMessage> SignInAsync(HttpClient client, FakeAuthenticator passkey)
    {
        var options = await client.PostAsync("/api/auth/sign-in/options", null);
        options.EnsureSuccessStatusCode();
        var credential = passkey.Get(await options.Content.ReadAsStringAsync());
        return await client.PostAsJsonAsync("/api/auth/sign-in", new { credentialJson = credential });
    }

    private readonly SemaphoreSlim _ownerLock = new(1, 1);
    private FakeAuthenticator? _ownerPasskey;

    /// <summary>
    /// A client signed in as the owner. The owner registers once per factory, with the bootstrap link; every
    /// further call signs a new browser in with that passkey, as a second visit would.
    /// </summary>
    public async Task<(HttpClient Client, FakeAuthenticator Passkey)> OwnerAsync(string name = "Owner")
    {
        await _ownerLock.WaitAsync();
        try
        {
            var client = NewBrowser();
            if (_ownerPasskey is null)
            {
                (_ownerPasskey, _) = await RegisterAsync(client, BootstrapToken, name);
            }
            else
            {
                (await SignInAsync(client, _ownerPasskey)).EnsureSuccessStatusCode();
            }

            return (client, _ownerPasskey);
        }
        finally
        {
            _ownerLock.Release();
        }
    }

    /// <summary>Mints an invite as the owner and returns the raw token the link carries.</summary>
    public static async Task<(Guid Id, string Token)> MintInviteAsync(HttpClient owner, string? label = null)
    {
        var response = await owner.PostAsJsonAsync("/api/invites", new { label });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    /// <summary>A member, invited by <paramref name="owner"/>, registered and signed in on the returned client.</summary>
    public async Task<(HttpClient Client, FakeAuthenticator Passkey)> MemberAsync(HttpClient owner, string name = "Member")
    {
        var (_, token) = await MintInviteAsync(owner);
        var client = NewBrowser();
        var (passkey, _) = await RegisterAsync(client, token, name);
        return (client, passkey);
    }
}

file sealed class SaveHooks(BasketFactory factory) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var entries = eventData.Context!.ChangeTracker;
        if (entries.Entries<LoyaltyCard>().Any(e => e.State == EntityState.Added) && factory.BeforeCardInsert is { } cardHook)
        {
            factory.BeforeCardInsert = null;
            await cardHook();
        }

        if (entries.Entries<PaidPrice>().Any(e => e.State == EntityState.Added) && factory.BeforePaidPriceInsert is { } paidHook)
        {
            factory.BeforePaidPriceInsert = null;
            await paidHook();
        }

        return result;
    }
}
