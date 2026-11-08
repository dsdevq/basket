using System.Net.Http.Json;
using System.Text.Json;
using Basket.Api.Auth;
using Basket.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
public sealed class BasketFactory : WebApplicationFactory<Program>
{
    public const string Origin = "https://basket.test";
    public const string BootstrapToken = "test-bootstrap-token-0123456789abcdef0123456789";

    private readonly string _adminConnection = Environment.GetEnvironmentVariable("BASKET_TEST_POSTGRES")
        ?? throw new InvalidOperationException(
            "BASKET_TEST_POSTGRES is not set. Example: Host=localhost;Port=55432;Username=basket;Password=basket-dev;Database=postgres");

    private readonly string _database = $"basket_test_{Guid.NewGuid():N}";

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public int AuthRequestsPerMinute { get; init; } = 1000;

    public string? Bootstrap { get; init; } = BootstrapToken;

    /// <summary>The fleet identity's issuer and signing key, as the app is configured to trust them. Off when false.</summary>
    public bool FleetAgentEnabled { get; init; } = true;

    public FleetTokens Fleet { get; } = new();

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
        builder.UseSetting("Hangfire:Enabled", "false");
        if (Bootstrap is not null)
        {
            builder.UseSetting("Auth:BootstrapToken", Bootstrap);
        }

        if (FleetAgentEnabled)
        {
            builder.UseSetting("FleetAgent:Authority", FleetTokens.Issuer);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (FleetAgentEnabled)
            {
                // The identity service's discovery document, replaced by a fixed one holding the test key.
                var configuration = new OpenIdConnectConfiguration { Issuer = FleetTokens.Issuer };
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
