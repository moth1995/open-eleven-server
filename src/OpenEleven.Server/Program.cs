using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;
using OpenEleven.Server.Transport;
using OpenEleven.Server.Web;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration: YAML file, an optional local override, then OPENELEVEN_-prefixed
// environment variables (OPENELEVEN_Server__Database__ConnectionString and friends).
// ---------------------------------------------------------------------------
var configPath = GetConfigPath(args) ?? Path.Combine(AppContext.BaseDirectory, "conf", "server.yaml");

builder.Configuration
    .AddYamlFile(configPath, optional: false, reloadOnChange: true)
    .AddYamlFile(Path.ChangeExtension(configPath, ".local.yaml"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("OPENELEVEN_");

builder.Services.AddOpenElevenCore(builder.Configuration);
builder.Services.AddHostedService<RoomWatchPublisher>();

// Serves the registration form at /register. Razor Pages ships in the shared framework,
// so this needs no package reference.
builder.Services.AddRazorPages();

// Antiforgery tokens are protected with Data Protection keys, which default to a per-user
// directory that a container does not persist. Without this, every restart invalidates the
// tokens on already-open registration pages and their next submit fails.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(AppContext.BaseDirectory, "data", "keys")));

var startupOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()
                     ?? new ServerOptions();

// ---------------------------------------------------------------------------
// One TCP listener per logical service. Only the gate port is fixed, because the
// client hardcodes it; every other port is discovered from the server list.
// ---------------------------------------------------------------------------
foreach (var endpoint in startupOptions.Services.Where(s => s.Enabled))
{
    builder.Services.AddSingleton<IHostedService>(sp => new GameListener(
        endpoint,
        sp.GetRequiredService<Hub>(),
        sp.GetRequiredService<ProtocolCodecs>(),
        sp.GetRequiredService<CommandDispatcher>(),
        sp.GetRequiredService<IOptionsMonitor<ServerOptions>>(),
        sp.GetRequiredService<ILoggerFactory>()));
}

// ---------------------------------------------------------------------------
// Kestrel serves the small HTTP surface the client fetches before logging in.
// ---------------------------------------------------------------------------
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(startupOptions.Listen.Http, listen => listen.Protocols = HttpProtocols.Http1);

    var https = startupOptions.Https;
    if (!https.Enabled)
        return;

    if (string.IsNullOrWhiteSpace(https.CertificatePath) || !File.Exists(https.CertificatePath))
    {
        // Deliberately no plaintext fallback on 443: a silent downgrade hides a
        // misconfiguration that resurfaces later as an unexplained client failure.
        Console.Error.WriteLine(
            $"[warn] HTTPS disabled: certificate '{https.CertificatePath}' not found. " +
            $"Port {startupOptions.Listen.Https} will not be served.");
        return;
    }

    kestrel.ListenAnyIP(startupOptions.Listen.Https, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.UseHttps(https.CertificatePath!, https.CertificatePassword);
    });
});

var app = builder.Build();

await InitializeDatabaseAsync(app);
LogStartupSummary(app, startupOptions);

// The ranked list of commands the client wanted and did not get is the reverse-engineering
// worklist, so it is printed once on the way out rather than lost in the running log.
app.Lifetime.ApplicationStopping.Register(
    () => app.Services.GetRequiredService<UnknownCommandLog>().Report());

// Static files first, so stylesheet requests short-circuit before the hex-dump trace.
// Note this now runs ahead of the /{file} WebAssets endpoint: a file dropped into wwwroot
// whose name collides with a WebAssets key would shadow it.
app.UseStaticFiles();

// Explicit, not implicit. HttpTraceMiddleware decides whether to redact a body by reading
// the matched endpoint's metadata, so routing has to have run by the time it is reached —
// that is a security property and should not depend on WebApplication's implicit ordering.
app.UseRouting();

app.UseMiddleware<HttpTraceMiddleware>();

app.MapRazorPages();
app.MapGameEndpoints();

await app.RunAsync();
return;

// ---------------------------------------------------------------------------

static string? GetConfigPath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] is "--config" or "-c")
            return Path.GetFullPath(args[i + 1]);
    return null;
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue;
    if (!options.Database.AutoCreate)
        return;

    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(options.Database.Seed);
}

static void LogStartupSummary(WebApplication app, ServerOptions options)
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenEleven.Startup");
    var catalog = app.Services.GetRequiredService<ServerCatalog>();
    var registry = app.Services.GetRequiredService<CommandRegistry>();

    log.LogInformation(
        "Advertising {Ip} to clients (configured as {Configured})",
        catalog.AdvertiseIp, options.AdvertiseIp);
    log.LogInformation("{Count} commands registered", registry.Count);

    foreach (var service in options.Services.Where(s => s.Enabled))
        log.LogInformation(
            "  {Role,-8} gid={Gid} port={Port} advertise={Advertise}",
            service.Role, service.Gid, service.Port, service.Advertise);
}
