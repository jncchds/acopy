using ACopy.Api.Data;
using ACopy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Docker;

public record LabelScanIssue(string Container, string Error);

/// <summary>
/// Periodically turns labelled containers into backup targets (Source = DockerLabel). Labels are the source of
/// truth for configuration; the Enabled switch stays the user's. A target whose container disappears (or loses
/// its enable label) is marked Orphaned — kept for its history, never scheduled.
/// </summary>
public class LabelDiscovery(
    DockerClient docker, IServiceScopeFactory scopes, BackupRunner runner,
    IOptions<DockerOptions> opt, ILogger<LabelDiscovery> log) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DateTimeOffset? LastScan { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<LabelScanIssue> Issues { get; private set; } = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opt.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, opt.Value.ScanSeconds)));
        do { await ScanAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ScanAsync(CancellationToken ct)
    {
        if (!docker.Configured) { LastError = "Docker is not configured (no socket mounted and no DOCKER_HOST)"; return; }
        await _gate.WaitAsync(ct);
        try
        {
            var containers = await docker.ListContainersAsync(ct);
            var issues = new List<LabelScanIssue>();
            var configs = new Dictionary<string, LabelConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in containers)
            {
                var (parsed, errors) = LabelConfig.Parse(opt.Value.LabelPrefix, c.Labels, c.Name, c.Image);
                issues.AddRange(errors.Select(e => new LabelScanIssue(c.Name, e)));
                foreach (var cfg in parsed)
                    if (!configs.TryAdd(cfg.Key, cfg))
                        issues.Add(new(c.Name, $"duplicate name '{cfg.Key}' — set a unique {opt.Value.LabelPrefix}.name"));
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var keys = await db.Keys.AsNoTracking().ToDictionaryAsync(k => k.Name, k => k.Id, StringComparer.OrdinalIgnoreCase, ct);
            var dests = await db.Destinations.AsNoTracking().ToDictionaryAsync(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase, ct);
            var existing = await db.Targets.Include(t => t.Destinations).Where(t => t.Source == TargetSource.DockerLabel).ToListAsync(ct);
            var byKey = existing.ToDictionary(t => t.LabelKey!, StringComparer.OrdinalIgnoreCase);

            foreach (var cfg in configs.Values)
            {
                int? keyId = null;
                if (cfg.EncryptionKey != null)
                {
                    if (keys.TryGetValue(cfg.EncryptionKey, out var kid)) keyId = kid;
                    else { issues.Add(new(cfg.ContainerName, $"{cfg.Name}: unknown encryption key '{cfg.EncryptionKey}' — target skipped until it exists")); continue; }
                }
                var destIds = new List<int>();
                foreach (var dn in cfg.Destinations)
                {
                    if (dests.TryGetValue(dn, out var did)) destIds.Add(did);
                    else issues.Add(new(cfg.ContainerName, $"{cfg.Name}: unknown destination '{dn}' — ignored"));
                }

                if (!byKey.TryGetValue(cfg.Key, out var t))
                {
                    // A manual target may already use this name — label targets get a suffix rather than failing
                    var name = cfg.Name;
                    if (await db.Targets.AnyAsync(x => x.Name == name, ct)) name = $"{cfg.Name} (docker)";
                    t = new BackupTarget { Name = name, Folder = cfg.Folder, Source = TargetSource.DockerLabel, LabelKey = cfg.Key };
                    db.Targets.Add(t);
                    log.LogInformation("Discovered labelled container {Container} → target {Name}", cfg.ContainerName, name);
                }
                Apply(t, cfg, keyId, destIds, runner);
            }

            foreach (var t in existing.Where(t => !configs.ContainsKey(t.LabelKey!) && !t.Orphaned))
            {
                t.Orphaned = true;
                t.UpdatedAt = DateTimeOffset.UtcNow;
                log.LogInformation("Labelled target {Name} lost its container — marked orphaned", t.Name);
            }

            await db.SaveChangesAsync(ct);
            Issues = issues;
            LastError = null;
            LastScan = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            LastError = e.Message;
            log.LogWarning(e, "Docker label scan failed");
        }
        finally { _gate.Release(); }
    }

    private static void Apply(BackupTarget t, LabelConfig c, int? keyId, List<int> destIds, BackupRunner runner)
    {
        var protectedPassword = c.Password == null ? null
            : runner.Unprotect(t.PasswordProtected) == c.Password ? t.PasswordProtected : runner.Protect(c.Password);
        var currentDests = t.Destinations.Select(d => d.DestinationId).Order().ToList();

        var changed =
            t.Engine != c.Engine || t.Mode != c.Mode || t.Host != c.Host || t.Port != c.Port || t.ContainerName != c.ContainerName ||
            t.Username != c.User || t.PasswordProtected != protectedPassword || t.PasswordEnv != c.PasswordEnv ||
            t.DatabaseName != c.Database || t.ExtraArgs != c.Args || t.CustomExtension != c.Extension ||
            t.Schedule != c.Schedule || t.Keep != c.Keep || t.MaxAgeDays != c.MaxAgeDays || t.Naming != c.Naming ||
            t.Compression != c.Compression || t.Folder != c.Folder || t.TimeoutMinutes != c.TimeoutMinutes ||
            t.EncryptionKeyId != keyId || t.RestoreCommand != c.RestoreCommand || t.TelegramChatIds != c.TelegramChats ||
            !currentDests.SequenceEqual(destIds.Distinct().Order()) || t.Orphaned;
        if (!changed) return;

        t.Engine = c.Engine; t.Mode = c.Mode; t.Host = c.Host; t.Port = c.Port; t.ContainerName = c.ContainerName;
        t.Username = c.User; t.PasswordProtected = protectedPassword; t.PasswordEnv = c.PasswordEnv;
        t.DatabaseName = c.Database; t.ExtraArgs = c.Args; t.CustomExtension = c.Extension;
        t.Schedule = c.Schedule; t.Keep = c.Keep; t.MaxAgeDays = c.MaxAgeDays; t.Naming = c.Naming; t.Compression = c.Compression;
        t.Folder = c.Folder; t.TimeoutMinutes = c.TimeoutMinutes; t.Orphaned = false;
        t.EncryptionKeyId = keyId; t.RestoreCommand = c.RestoreCommand; t.TelegramChatIds = c.TelegramChats;
        t.Destinations.RemoveAll(d => !destIds.Contains(d.DestinationId));
        foreach (var id in destIds.Distinct().Where(id => t.Destinations.All(d => d.DestinationId != id)))
            t.Destinations.Add(new TargetDestination { DestinationId = id });
        t.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
