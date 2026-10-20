using System.Text.Json.Serialization;
using ACopy.Api;
using ACopy.Api.Data;
using ACopy.Api.Docker;
using ACopy.Api.Endpoints;
using ACopy.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ACOPY_Section__Key environment variables override appsettings.json
builder.Configuration.AddEnvironmentVariables("ACOPY_");

builder.Services.Configure<PathsOptions>(builder.Configuration.GetSection("Paths"));
builder.Services.Configure<DockerOptions>(builder.Configuration.GetSection("Docker"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));

var paths = builder.Configuration.GetSection("Paths").Get<PathsOptions>() ?? new PathsOptions();
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.Database))!);
Directory.CreateDirectory(paths.Backups);

builder.Services.AddDbContext<AppDbContext>(o => o
    .UseSqlite($"Data Source={paths.Database};Pooling=False")
    .AddInterceptors(new BusyTimeoutInterceptor()));
// Target passwords are encrypted with a key ring stored in the same SQLite file — one file to back up.
builder.Services.AddDataProtection().SetApplicationName("ACopy").PersistKeysToDbContext<AppDbContext>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DockerClient>();
builder.Services.AddSingleton<Secrets>();
builder.Services.AddSingleton<ACopy.Api.Destinations.TelegramClient>();
builder.Services.AddSingleton<ACopy.Api.Destinations.DestinationService>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<BackupRunner>();
builder.Services.AddSingleton<ConnectionTester>();
builder.Services.AddSingleton<SystemInfo>();
builder.Services.AddSingleton<LabelDiscovery>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LabelDiscovery>());
builder.Services.AddHostedService<Scheduler>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "acopy.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = null;
    o.DefaultPolicy = authOptions.Disabled
        ? new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build()
        : new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
});

var app = builder.Build();

// ── Start-up: migrate, settle the journal mode, apply the env password, recover interrupted runs ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    // Rollback-journal mode keeps everything in the one file between transactions, so a single-file bind
    // mount works and the file is always complete when copied (WAL would leave data in a -wal side file).
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
}
var settingsStore = app.Services.GetRequiredService<SettingsStore>();
if (!string.IsNullOrEmpty(authOptions.Password))
    await settingsStore.SetAsync(SettingsStore.KeyPasswordHash, SettingsStore.HashPassword(authOptions.Password));
await app.Services.GetRequiredService<BackupRunner>().RecoverAsync();
var telegramOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>().Value;
if (string.IsNullOrWhiteSpace(telegramOptions.UploadDir) != string.IsNullOrWhiteSpace(telegramOptions.ServerUploadDir))
    app.Logger.LogWarning("Set both ACOPY_Telegram__UploadDir and ACOPY_Telegram__ServerUploadDir to send Telegram files by path; uploading over HTTP");
app.Services.GetRequiredService<ACopy.Api.Destinations.TelegramClient>().CleanStaging(app.Logger);
if (authOptions.Disabled) app.Logger.LogWarning("Authentication is DISABLED (ACOPY_Auth__Disabled=true)");

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = AppVersion.Value }));

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
var secured = api.MapGroup("").RequireAuthorization();
secured.MapSystemEndpoints();
secured.MapTargetEndpoints();
secured.MapRunEndpoints();
secured.MapDockerEndpoints();
secured.MapSettingsEndpoints();
secured.MapStorageEndpoints();
api.MapFallback(() => Results.NotFound());

// SPA: every non-API route serves index.html
app.MapFallbackToFile("index.html");

app.Run();
