using System.Text;
using ACopy.Api.Data;
using ACopy.Api.Destinations;
using ACopy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Endpoints;

public static class TargetEndpoints
{
    public static void MapTargetEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/targets");

        g.MapGet("/", async (AppDbContext db, BackupRunner runner, SettingsStore settings) =>
        {
            var targets = await db.Targets.AsNoTracking().Include(t => t.EncryptionKey).Include(t => t.Destinations).OrderBy(t => t.Name).ToListAsync();
            return await ToDtos(db, runner, settings, targets);
        });

        g.MapGet("/{id:int}", async (int id, AppDbContext db, BackupRunner runner, SettingsStore settings) =>
        {
            var t = await db.Targets.AsNoTracking().Include(x => x.EncryptionKey).Include(x => x.Destinations).FirstOrDefaultAsync(x => x.Id == id);
            return t == null ? Results.NotFound() : Results.Ok((await ToDtos(db, runner, settings, [t]))[0]);
        });

        g.MapPost("/", async (TargetInput input, AppDbContext db, BackupRunner runner, SettingsStore settings) =>
        {
            var errors = input.Validate();
            if (await db.Targets.AnyAsync(t => t.Name == input.Name.Trim())) errors["name"] = ["A target with this name already exists"];
            await ValidateRefs(db, input, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var t = new BackupTarget { Name = input.Name.Trim(), Folder = "" };
            Apply(t, input, runner);
            db.Targets.Add(t);
            await db.SaveChangesAsync();
            await db.Entry(t).Reference(x => x.EncryptionKey).LoadAsync();
            return Results.Ok((await ToDtos(db, runner, settings, [t]))[0]);
        });

        g.MapPut("/{id:int}", async (int id, TargetInput input, AppDbContext db, BackupRunner runner, SettingsStore settings) =>
        {
            var t = await db.Targets.Include(x => x.Destinations).FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return Results.NotFound();
            if (t.Source == TargetSource.DockerLabel) return Results.Problem("This target is configured by Docker labels — edit the labels instead.", statusCode: 409);
            var errors = input.Validate();
            if (await db.Targets.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim())) errors["name"] = ["A target with this name already exists"];
            await ValidateRefs(db, input, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var oldKeep = t.Keep;
            Apply(t, input, runner);
            if (t.Keep != oldKeep && t.NextSlot >= t.Keep) t.NextSlot = 0;
            t.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await db.Entry(t).Reference(x => x.EncryptionKey).LoadAsync();
            return Results.Ok((await ToDtos(db, runner, settings, [t]))[0]);
        });

        g.MapPost("/{id:int}/enabled", async (int id, EnabledInput input, AppDbContext db) =>
        {
            var t = await db.Targets.FindAsync(id);
            if (t == null) return Results.NotFound();
            t.Enabled = input.Enabled;
            t.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/{id:int}", async (int id, bool? deleteFiles, AppDbContext db, BackupRunner runner, IOptions<PathsOptions> paths) =>
        {
            var t = await db.Targets.FindAsync(id);
            if (t == null) return Results.NotFound();
            if (runner.IsActive(id)) return Results.Problem("A backup is running — cancel it first.", statusCode: 409);
            if (t.Source == TargetSource.DockerLabel && !t.Orphaned)
                return Results.Problem("This target comes from Docker labels — remove the labels (or the container) first, or pause it.", statusCode: 409);
            if (deleteFiles == true)
            {
                var folder = Path.Combine(paths.Value.Backups, BackupFiles.SafeFolder(t.Folder));
                if (Directory.Exists(folder))
                    foreach (var f in BackupFiles.OwnedFiles(t, Directory.EnumerateFiles(folder).Select(Path.GetFileName)!))
                        File.Delete(Path.Combine(folder, f));
            }
            db.Targets.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:int}/run", async (int id, AppDbContext db, BackupRunner runner) =>
        {
            if (!await db.Targets.AnyAsync(t => t.Id == id)) return Results.NotFound();
            var runId = await runner.StartAsync(id, RunTrigger.Manual);
            return runId == null ? Results.Problem("A backup of this target is already running.", statusCode: 409) : Results.Ok(new { runId });
        });

        g.MapPost("/{id:int}/cancel", (int id, BackupRunner runner) =>
            runner.Cancel(id) ? Results.NoContent() : Results.Problem("Nothing is running for this target.", statusCode: 409));

        g.MapPost("/{id:int}/test", async (int id, AppDbContext db, ConnectionTester tester, CancellationToken ct) =>
        {
            var t = await db.Targets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return t == null ? Results.NotFound() : Results.Ok(await tester.TestAsync(t, ct));
        });

        // Test unsaved form values. A blank password on an existing target falls back to the stored one.
        g.MapPost("/test", async (TargetInput input, int? id, AppDbContext db, BackupRunner runner, ConnectionTester tester, CancellationToken ct) =>
        {
            var t = new BackupTarget { Name = input.Name ?? "test", Folder = "test" };
            if (id != null && await db.Targets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) is { } existing)
                t.PasswordProtected = existing.PasswordProtected;
            Apply(t, input, runner);
            return Results.Ok(await tester.TestAsync(t, ct));
        });
    }

    private static void Apply(BackupTarget t, TargetInput i, BackupRunner runner)
    {
        t.Name = i.Name.Trim();
        t.Engine = i.Engine;
        t.Mode = i.Mode;
        t.Host = Blank(i.Host);
        t.Port = i.Port;
        t.ContainerName = Blank(i.ContainerName);
        t.Username = Blank(i.Username);
        if (i.ClearPassword == true) t.PasswordProtected = null;
        else if (!string.IsNullOrEmpty(i.Password)) t.PasswordProtected = runner.Protect(i.Password);
        t.PasswordEnv = Blank(i.PasswordEnv);
        t.DatabaseName = Blank(i.DatabaseName);
        t.ExtraArgs = Blank(i.ExtraArgs);
        t.CustomExtension = Blank(i.CustomExtension)?.TrimStart('.');
        t.Schedule = i.Schedule.Trim();
        t.Enabled = i.Enabled;
        t.Keep = i.Keep;
        t.Naming = i.Naming;
        t.Compression = i.Compression;
        t.Folder = BackupFiles.SafeFolder(string.IsNullOrWhiteSpace(i.Folder) ? t.Name : i.Folder);
        t.TimeoutMinutes = i.TimeoutMinutes ?? 120;
        t.MaxAgeDays = t.Naming == NamingMode.Timestamp ? i.MaxAgeDays : null;
        t.EncryptionKeyId = i.EncryptionKeyId;
        t.RestoreCommand = Blank(i.RestoreCommand);
        t.TelegramChatIds = NormalizeChats(i.TelegramChatIds);
        var wanted = (i.DestinationIds ?? []).Distinct().ToList();
        t.Destinations.RemoveAll(d => !wanted.Contains(d.DestinationId));
        foreach (var id in wanted.Where(id => t.Destinations.All(d => d.DestinationId != id)))
            t.Destinations.Add(new TargetDestination { DestinationId = id });
    }

    private static async Task ValidateRefs(AppDbContext db, TargetInput input, Dictionary<string, string[]> errors)
    {
        if (input.EncryptionKeyId is { } kid && !await db.Keys.AnyAsync(k => k.Id == kid)) errors["encryptionKeyId"] = ["Unknown encryption key"];
        foreach (var did in input.DestinationIds ?? [])
            if (!await db.Destinations.AnyAsync(d => d.Id == did)) { errors["destinationIds"] = ["Unknown destination"]; break; }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"1, 2,,3" → "1,2,3"; empty → null.</summary>
    internal static string? NormalizeChats(string? s) =>
        TelegramClient.ParseChats(s) is { Count: > 0 } chats ? string.Join(',', chats) : null;

    internal static async Task<List<TargetDto>> ToDtos(AppDbContext db, BackupRunner runner, SettingsStore settings, List<BackupTarget> targets)
    {
        var ids = targets.Select(t => t.Id).ToList();
        var tz = await settings.GetTimeZoneAsync();
        var now = DateTimeOffset.UtcNow;

        var lastRunIds = await db.Runs.Where(r => ids.Contains(r.TargetId))
            .GroupBy(r => r.TargetId).Select(g => g.Max(r => r.Id)).ToListAsync();
        var lastRuns = await db.Runs.AsNoTracking().Where(r => lastRunIds.Contains(r.Id)).ToDictionaryAsync(r => r.TargetId);
        var lastOk = (await db.Runs.Where(r => ids.Contains(r.TargetId) && r.Status == RunStatus.Succeeded && r.Kind == RunKind.Backup)
                .Select(r => new { r.TargetId, r.StartedAt }).ToListAsync())
            .GroupBy(r => r.TargetId).ToDictionary(g => g.Key, g => g.Max(r => r.StartedAt));
        var stored = (await db.Runs.Where(r => ids.Contains(r.TargetId) && r.Status == RunStatus.Succeeded && !r.FileGone && r.Kind == RunKind.Backup)
                .Select(r => new { r.TargetId, r.SizeBytes }).ToListAsync())
            .GroupBy(r => r.TargetId).ToDictionary(g => g.Key, g => (Bytes: g.Sum(r => r.SizeBytes ?? 0), Files: g.Count()));

        return targets.Select(t =>
        {
            var next = t.Enabled && !t.Orphaned ? Scheduler.NextOccurrence(t.Schedule, now, tz) : null;
            var last = lastRuns.TryGetValue(t.Id, out var r) ? RunDto.From(r, includeLog: false) : null;
            var s = stored.TryGetValue(t.Id, out var x) ? x : (0, 0);
            return TargetDto.From(t, next, runner.IsActive(t.Id), last, lastOk.TryGetValue(t.Id, out var ok) ? ok : null, s.Item1, s.Item2);
        }).ToList();
    }
}
