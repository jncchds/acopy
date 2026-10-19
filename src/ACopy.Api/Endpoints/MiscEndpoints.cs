using System.Security.Claims;
using ACopy.Api.Data;
using ACopy.Api.Docker;
using ACopy.Api.Engines;
using ACopy.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Endpoints;

public static class MiscEndpoints
{
    public static void MapRunEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/runs");

        g.MapGet("/", async (int? targetId, string? status, int? limit, AppDbContext db) =>
        {
            var q = db.Runs.AsNoTracking().Include(r => r.Target).Include(r => r.Uploads).ThenInclude(u => u.Destination).AsQueryable();
            if (targetId != null) q = q.Where(r => r.TargetId == targetId);
            if (Enum.TryParse<RunStatus>(status, true, out var s)) q = q.Where(r => r.Status == s);
            var rows = await q.OrderByDescending(r => r.Id).Take(Math.Clamp(limit ?? 100, 1, 1000)).ToListAsync();
            return rows.Select(r => RunDto.From(r, includeLog: false));
        });

        g.MapGet("/{id:long}", async (long id, AppDbContext db) =>
            await db.Runs.AsNoTracking().Include(r => r.Target).Include(r => r.Uploads).ThenInclude(u => u.Destination).FirstOrDefaultAsync(r => r.Id == id) is { } r
                ? Results.Ok(RunDto.From(r)) : Results.NotFound());

        g.MapGet("/{id:long}/download", async (long id, AppDbContext db, IOptions<PathsOptions> paths) =>
        {
            var r = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (r?.FilePath == null || r.FileGone) return Results.NotFound();
            var full = ResolveInsideBackups(paths.Value.Backups, r.FilePath);
            if (full == null || !File.Exists(full)) return Results.NotFound();
            return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
        });

        g.MapDelete("/{id:long}/file", async (long id, AppDbContext db, IOptions<PathsOptions> paths) =>
        {
            var r = await db.Runs.FirstOrDefaultAsync(x => x.Id == id);
            if (r?.FilePath == null || r.FileGone) return Results.NotFound();
            var full = ResolveInsideBackups(paths.Value.Backups, r.FilePath);
            if (full != null && File.Exists(full)) File.Delete(full);
            r.FileGone = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static string? ResolveInsideBackups(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    public static void MapSystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/meta", (IOptions<DockerOptions> docker) => new
        {
            version = AppVersion.Value,
            engines = EngineCatalog.All.Select(e => new
            {
                engine = e.Engine.ToString(), e.Label, e.DefaultPort, e.Extension, e.SupportsNetwork, e.SupportsExec,
                e.AllDatabasesWhenEmpty, e.DatabaseHint, e.DefaultUser, e.Notes,
            }),
            labelPrefix = docker.Value.LabelPrefix,
            labelKeys = LabelConfig.Keys.Select(k => new { key = k.Key, description = k.Description }),
        });

        api.MapGet("/dashboard", async (AppDbContext db, BackupRunner runner, SettingsStore settings, IOptions<PathsOptions> paths) =>
        {
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var targets = await db.Targets.AsNoTracking().ToListAsync();
            var dtos = await TargetEndpoints.ToDtos(db, runner, settings, targets);
            var recent = await db.Runs.AsNoTracking().Where(r => r.StartedAt >= since).Select(r => r.Status).ToListAsync();
            long? free = null;
            try { free = new DriveInfo(Path.GetFullPath(paths.Value.Backups)).AvailableFreeSpace; } catch { }
            return new
            {
                targets = dtos.Count,
                enabled = dtos.Count(t => t.Enabled && !t.Orphaned),
                failing = dtos.Count(t => t.LastRun?.Status == nameof(RunStatus.Failed)),
                active = dtos.Count(t => t.Active),
                succeeded24h = recent.Count(s => s == RunStatus.Succeeded),
                failed24h = recent.Count(s => s == RunStatus.Failed),
                storedBytes = dtos.Sum(t => t.StoredBytes),
                storedFiles = dtos.Sum(t => t.StoredFiles),
                freeBytes = free,
                upcoming = dtos.Where(t => t.NextRunAt != null).OrderBy(t => t.NextRunAt).Take(8)
                    .Select(t => new { t.Id, t.Name, t.Engine, t.NextRunAt }),
            };
        });

        api.MapGet("/system", async (DockerClient docker, LabelDiscovery discovery, SystemInfo info, SettingsStore settings,
            IOptions<PathsOptions> paths, IOptions<DockerOptions> dopt, CancellationToken ct) =>
        {
            string? dockerVersion = null, dockerError = null;
            try { dockerVersion = await docker.VersionAsync(ct); }
            catch (Exception e) { dockerError = e.Message; }
            if (dockerVersion == null && dockerError == null)
                dockerError = dopt.Value.Enabled ? $"{docker.Endpoint} is not reachable (socket not mounted?)" : "Disabled by configuration";
            return new
            {
                version = AppVersion.Value,
                timeZone = await settings.GetTimeZoneIdAsync(),
                backupsPath = paths.Value.Backups,
                databasePath = paths.Value.Database,
                docker = new
                {
                    available = dockerVersion != null, version = dockerVersion, error = dockerError,
                    socket = docker.Endpoint, labelPrefix = dopt.Value.LabelPrefix,
                    lastScan = discovery.LastScan, scanError = discovery.LastError, issues = discovery.Issues,
                },
                tools = await info.ToolsAsync(),
            };
        });
    }

    public static void MapDockerEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/docker");

        // The container picker: every container with its guessed engine and whether labels manage it.
        g.MapGet("/containers", async (DockerClient docker, IOptions<DockerOptions> opt, CancellationToken ct) =>
        {
            if (!docker.Configured) return Results.Problem("Docker socket is not mounted.", statusCode: 503);
            var list = await docker.ListContainersAsync(ct);
            var prefix = opt.Value.LabelPrefix;
            return Results.Ok(list.OrderBy(c => c.Name).Select(c => new
            {
                c.Id, c.Name, c.Image, c.State, c.Status,
                detectedEngine = EngineCatalog.DetectFromImage(c.Image)?.ToString(),
                labelled = c.Labels.TryGetValue($"{prefix}.enable", out var en) && en.Equals("true", StringComparison.OrdinalIgnoreCase),
                acopyLabels = c.Labels.Where(l => l.Key.StartsWith(prefix + ".")).ToDictionary(l => l.Key, l => l.Key.EndsWith(".password") ? "••••" : l.Value),
            }));
        });

        // Credentials/database the image's env suggests — used to prefill the form (password is not returned).
        g.MapGet("/containers/{name}/detect", async (string name, DockerClient docker, CancellationToken ct) =>
        {
            if (!docker.Configured) return Results.Problem("Docker socket is not mounted.", statusCode: 503);
            var c = await docker.InspectAsync(name, ct);
            if (c == null) return Results.NotFound();
            var engine = EngineCatalog.DetectFromImage(c.Image);
            var creds = engine == null ? null : EngineCatalog.DetectCredentials(engine.Value, c.Env);
            return Results.Ok(new
            {
                engine = engine?.ToString(), user = creds?.User, hasPassword = creds?.Password != null, database = creds?.Database,
                c.Running,
            });
        });

        g.MapPost("/scan", async (LabelDiscovery discovery, CancellationToken ct) =>
        {
            await discovery.ScanAsync(ct);
            return Results.Ok(new { discovery.LastScan, discovery.LastError, discovery.Issues });
        });
    }

    public static void MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/settings", (SettingsStore s) => s.GetPublicAsync());

        api.MapPut("/settings", async (SettingsInput input, SettingsStore s, Secrets secrets) =>
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZone.Trim(), out _))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeZone"] = ["Unknown time zone (use an IANA name like Europe/Berlin)"] });
            if (!string.IsNullOrWhiteSpace(input.WebhookUrl) && !Uri.TryCreate(input.WebhookUrl.Trim(), UriKind.Absolute, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["webhookUrl"] = ["Not a valid URL"] });
            await s.SetAsync(SettingsStore.KeyTimeZone, input.TimeZone.Trim());
            await s.SetAsync(SettingsStore.KeyWebhookUrl, string.IsNullOrWhiteSpace(input.WebhookUrl) ? null : input.WebhookUrl.Trim());
            await s.SetAsync(SettingsStore.KeyNotifyOnSuccess, input.NotifyOnSuccess ? "true" : "false");
            if (input.ClearTelegramToken == true) await s.SetAsync(SettingsStore.KeyTelegramToken, null);
            else if (!string.IsNullOrWhiteSpace(input.TelegramToken)) await s.SetAsync(SettingsStore.KeyTelegramToken, secrets.Protect(input.TelegramToken.Trim()));
            await s.SetAsync(SettingsStore.KeyTelegramChat, Blank(input.TelegramChatId));
            await s.SetAsync(SettingsStore.KeyTelegramThread, Blank(input.TelegramThreadId));
            await s.SetAsync(SettingsStore.KeyTelegramApi, Blank(input.TelegramApiUrl));
            return Results.Ok(await s.GetPublicAsync());
        });

        api.MapPost("/settings/test-webhook", async (SettingsStore s, Notifier n) =>
        {
            var url = await s.GetAsync(SettingsStore.KeyWebhookUrl);
            if (string.IsNullOrWhiteSpace(url)) return Results.Problem("No webhook URL saved.", statusCode: 400);
            var err = await n.SendTestAsync(url);
            return err == null ? Results.NoContent() : Results.Problem($"Webhook failed: {err}", statusCode: 502);
        });

        api.MapPost("/settings/test-telegram", async (Notifier n) =>
            await n.SendTelegramTestAsync() is { } err ? Results.Problem($"Telegram failed: {err}", statusCode: 502) : Results.NoContent());

        api.MapGet("/timezones", () => TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Order());
    }

    /// <summary>Single-admin password auth (cookie). Mapped outside the authorized group.</summary>
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapGet("/state", async (HttpContext ctx, SettingsStore s, IOptions<AuthOptions> auth) => new
        {
            disabled = auth.Value.Disabled,
            setupRequired = !auth.Value.Disabled && string.IsNullOrEmpty(await s.GetAsync(SettingsStore.KeyPasswordHash)),
            authenticated = auth.Value.Disabled || ctx.User.Identity?.IsAuthenticated == true,
            passwordFromEnv = !string.IsNullOrEmpty(auth.Value.Password),
        });

        g.MapPost("/setup", async (PasswordInput input, HttpContext ctx, SettingsStore s) =>
        {
            if (!string.IsNullOrEmpty(await s.GetAsync(SettingsStore.KeyPasswordHash))) return Results.Problem("Already set up.", statusCode: 409);
            if (input.Password.Length < 8) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["At least 8 characters"] });
            await s.SetAsync(SettingsStore.KeyPasswordHash, SettingsStore.HashPassword(input.Password));
            await SignInAsync(ctx);
            return Results.NoContent();
        });

        g.MapPost("/login", async (PasswordInput input, HttpContext ctx, SettingsStore s) =>
        {
            if (!SettingsStore.VerifyPassword(input.Password, await s.GetAsync(SettingsStore.KeyPasswordHash)))
            {
                await Task.Delay(500);
                return Results.Problem("Wrong password.", statusCode: 401);
            }
            await SignInAsync(ctx);
            return Results.NoContent();
        });

        g.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        g.MapPost("/password", async (ChangePasswordInput input, SettingsStore s, IOptions<AuthOptions> auth) =>
        {
            if (!string.IsNullOrEmpty(auth.Value.Password)) return Results.Problem("The password is set by ACOPY_Auth__Password — change it there.", statusCode: 409);
            if (!SettingsStore.VerifyPassword(input.Current, await s.GetAsync(SettingsStore.KeyPasswordHash))) return Results.Problem("Current password is wrong.", statusCode: 400);
            if (input.Password.Length < 8) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["At least 8 characters"] });
            await s.SetAsync(SettingsStore.KeyPasswordHash, SettingsStore.HashPassword(input.Password));
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Task SignInAsync(HttpContext ctx) => ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true });
}

public static class AppVersion
{
    public static readonly string Value = ReadVersion();

    private static string ReadVersion()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "VERSION"), "VERSION", "../../VERSION" })
            if (File.Exists(p)) return File.ReadAllText(p).Trim();
        return "dev";
    }
}
