using Basket.Api.Auth;
using Basket.Api.Cards;
using Basket.Api.Data;
using Basket.Api.Infrastructure;
using Basket.Api.LidlPlus;
using Basket.Api.Lists;
using Basket.Api.Matching;
using Basket.Api.Plan;
using Basket.Api.Preferences;
using Basket.Api.Prices;
using Basket.Api.Prices.Ingest;
using Basket.Api.Prices.Fetching;
using Basket.Api.Prices.Promotions;
using Basket.Api.Prices.Sources;
using Basket.Api.Scorecard;
using Basket.Api.Walk;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    // One compact JSON object per line on stdout, with the W3C trace id as @tr: the platform contract's logs item.
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<InviteCleanupJob>();

builder.Services.Configure<PricesOptions>(builder.Configuration.GetSection(PricesOptions.Section));
builder.Services.AddSingleton<HostThrottle>();
builder.Services.AddHttpClient<IPageFetcher, PoliteFetcher>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PricesOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
    // Honest and ordinary: who is asking, why, and how little. Nothing here pretends to be a browser.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("basket-price-check/1.0 (a household app reading its own products' prices once a night)");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-IE,en;q=0.8");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    MaxAutomaticRedirections = 3,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
});
builder.Services.AddScoped<IStorePriceSource, LidlPriceSource>();
builder.Services.AddScoped<PriceRecorder>();
builder.Services.AddScoped<PricePullJob>();
builder.Services.AddScoped<IngestTokenService>();
builder.Services.AddSingleton<CardNumberProtector>();
builder.Services.Configure<LidlPlusOptions>(builder.Configuration.GetSection(LidlPlusOptions.Section));
builder.Services.AddSingleton<LidlPlusTokenProtector>();
builder.Services.AddHttpClient<ILidlPlusAuth, LidlPlusAuthClient>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LidlPlusOptions>>().Value;
    client.BaseAddress = new Uri(options.AccountsBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 64 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("basket/1.0 (a household app keeping its own Lidl Plus sign-in alive)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<LidlPlusConnectionService>();
builder.Services.AddHttpClient<ILidlPlusTickets, LidlPlusTicketsClient>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LidlPlusOptions>>().Value;
    client.BaseAddress = new Uri(options.TicketsBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("basket/1.0 (a household app reading its own Lidl Plus receipts once a night)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<LidlReceiptImport>();
builder.Services.AddScoped<LidlReceiptsJob>();
builder.Services.AddScoped<PriceIngest>();
builder.Services.AddBasketAuth(builder.Configuration);
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));
builder.Services.AddHostedService<OwnerBootstrap>();

// The cookie and the passkey ceremony state are protected with these keys; they live on a volume so a
// restart or a redeploy does not sign everyone out.
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
if (!string.IsNullOrEmpty(keyDirectory))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("basket")
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Tailscale Funnel reaches the container through the host's loopback and the Docker bridge, so only
    // private-network peers may tell us the client address.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7" })
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

// Metrics for the platform contract: ASP.NET request counters and runtime gauges, as Prometheus text on /metrics.
// The meters are process-wide, so a test host that builds one meter provider each (dozens of hosts side by side) holds
// them all in memory; the tests turn this off except where they read /metrics.
var metricsEnabled = builder.Configuration.GetValue("Observability:Metrics:Enabled", true);
if (metricsEnabled)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("basket"))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter());
}

builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(storage => storage.UseNpgsqlConnection(connectionString)));
    builder.Services.AddHangfireServer(options => options.WorkerCount = 2);
}

var app = builder.Build();

// Before the forwarded-headers middleware, which consumes X-Forwarded-For: /metrics is for Prometheus on the docker network.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(Observability.MetricsPath) && Observability.CameThroughProxy(context.Request),
    branch => branch.Run(context =>
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }));
app.UseForwardedHeaders();
app.UseSerilogRequestLogging(options =>
{
    RequestLogging.Configure(options);
    // This host's own logger, not Serilog's process-wide static one: hosts built side by side (the integration tests)
    // would otherwise log each other's requests to whichever logger was set last.
    options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
    options.GetLevel = (context, _, ex) =>
        ex is not null || context.Response.StatusCode >= 500
            ? Serilog.Events.LogEventLevel.Error
            : context.Request.Path.StartsWithSegments("/api/health") && !Observability.IsLoggedHealthProbe(context.Request)
                ? Serilog.Events.LogEventLevel.Verbose
                : Serilog.Events.LogEventLevel.Information;
});
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHsts();
app.UseMiddleware<OriginCheckMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Hashed bundles never change; the shell and the service worker must be re-checked every time.
        var hashed = System.Text.RegularExpressions.Regex.IsMatch(context.File.Name, @"-[A-Z0-9]{8}\.(js|css|woff2)$");
        context.Context.Response.Headers.CacheControl = hashed ? "public, max-age=31536000, immutable" : "no-cache";
    },
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
api.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
api.MapAuthEndpoints();
api.MapPasskeyEndpoints();
api.MapListEndpoints();
api.MapTripEndpoints();
api.MapHabitEndpoints();
api.MapProductEndpoints();
api.MapMatchEndpoints();
api.MapPromotionEndpoints();
api.MapPlanEndpoints();
api.MapScorecardEndpoints();
api.MapWalkEndpoints();
api.MapCardEndpoints();
api.MapPreferencesEndpoints();
api.MapLidlPlusEndpoints();
api.MapIngestEndpoints();
api.MapFallback(() => Results.NotFound()).AllowAnonymous();
if (metricsEnabled)
{
    app.MapPrometheusScrapingEndpoint(Observability.MetricsPath).AllowAnonymous();
}

app.MapFallbackToFile("index.html");

if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    InviteCleanupJob.Schedule(app.Services.GetRequiredService<IRecurringJobManager>());
    PricePullJob.Schedule(app.Services.GetRequiredService<IRecurringJobManager>());
    LidlReceiptsJob.Schedule(app.Services.GetRequiredService<IRecurringJobManager>());
}

app.Run();

public partial class Program;
