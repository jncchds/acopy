using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ACopy.Api.Data;
using ACopy.Api.Docker;
using ACopy.Api.Engines;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Services;

/// <summary>
/// Executes backups: resolves credentials, runs the dump (locally or via docker exec), compresses into a
/// Syncthing-invisible temp file, renames atomically, applies retention, records the run, notifies.
/// One run per target at a time; at most <see cref="BackupOptions.MaxParallel"/> runs overall.
/// </summary>
public class BackupRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DockerClient _docker;
    private readonly IDataProtector _protector;
    private readonly SettingsStore _settings;
    private readonly Notifier _notifier;
    private readonly PathsOptions _paths;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupRunner> _log;
    private readonly SemaphoreSlim _parallel;
    private readonly ConcurrentDictionary<int, (long RunId, CancellationTokenSource Cts)> _active = new();

    public BackupRunner(
        IServiceScopeFactory scopes, DockerClient docker, IDataProtectionProvider dp, SettingsStore settings,
        Notifier notifier, IOptions<PathsOptions> paths, IOptions<BackupOptions> opt, ILogger<BackupRunner> log)
    {
        _scopes = scopes;
        _docker = docker;
        _protector = dp.CreateProtector("ACopy.TargetPassword");
        _settings = settings;
        _notifier = notifier;
        _paths = paths.Value;
        _opt = opt.Value;
        _log = log;
        _parallel = new SemaphoreSlim(Math.Max(1, _opt.MaxParallel));
    }

    public string Protect(string plain) => _protector.Protect(plain);
    public string? Unprotect(string? cipher) => string.IsNullOrEmpty(cipher) ? null : _protector.Unprotect(cipher);

    public bool IsActive(int targetId) => _active.ContainsKey(targetId);
    public IReadOnlyCollection<int> ActiveTargetIds => _active.Keys.ToList();

    public bool Cancel(int targetId)
    {
        if (!_active.TryGetValue(targetId, out var a)) return false;
        a.Cts.Cancel();
        return true;
    }

    /// <summary>Queue a run. Returns the run id, or null when the target already has one in flight.</summary>
    public async Task<long?> StartAsync(int targetId, RunTrigger trigger)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var target = await db.Targets.FindAsync(targetId) ?? throw new KeyNotFoundException();

        var cts = new CancellationTokenSource();
        var run = new BackupRun { TargetId = targetId, Status = RunStatus.Queued, Trigger = trigger, StartedAt = DateTimeOffset.UtcNow };
        if (!_active.TryAdd(targetId, (0, cts))) { cts.Dispose(); return null; }
        try
        {
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            _active[targetId] = (run.Id, cts);
        }
        catch
        {
            _active.TryRemove(targetId, out _);
            cts.Dispose();
            throw;
        }

        _ = Task.Run(() => ExecuteAsync(targetId, run.Id, cts));
        return run.Id;
    }

    private async Task ExecuteAsync(int targetId, long runId, CancellationTokenSource cts)
    {
        try
        {
            await _parallel.WaitAsync(cts.Token);
            try { await RunCoreAsync(targetId, runId, cts.Token); }
            finally { _parallel.Release(); }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(runId, RunStatus.Cancelled, "Cancelled before it started", null);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Backup run {RunId} crashed", runId);
            await FinishAsync(runId, RunStatus.Failed, e.Message, null);
        }
        finally
        {
            _active.TryRemove(targetId, out _);
            cts.Dispose();
        }
    }

    private async Task RunCoreAsync(int targetId, long runId, CancellationToken cancel)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var target = await db.Targets.FirstAsync(t => t.Id == targetId);
        var run = await db.Runs.FirstAsync(r => r.Id == runId);
        run.Status = RunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, target.TimeoutMinutes)));
        var ct = timeout.Token;

        var log = new StringBuilder();
        var tz = await _settings.GetTimeZoneAsync();
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime;
        var folderRel = BackupFiles.SafeFolder(target.Folder);
        var folder = Path.Combine(_paths.Backups, folderRel);
        Directory.CreateDirectory(folder);

        var slot = target.Naming == NamingMode.Rolling ? Math.Clamp(target.NextSlot, 0, Math.Max(1, target.Keep) - 1) : (int?)null;
        var fileName = BackupFiles.FileName(target, localNow, slot ?? 0);
        var finalPath = Path.Combine(folder, fileName);
        var tempPath = Path.Combine(folder, BackupFiles.TempName(fileName));
        var workDir = Path.Combine(Path.GetTempPath(), "acopy");
        Directory.CreateDirectory(workDir);

        DumpCommand? cmd = null;
        try
        {
            var (creds, containerId) = await ResolveAsync(target, log, ct);
            cmd = DumpCommandBuilder.Build(target, creds, workDir);
            long rawBytes;

            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await using var compressed = Compress(file, target.Compression);
                var counting = new CountingStream(compressed);
                if (cmd.SqliteSourcePath != null) await SqliteBackupAsync(cmd.SqliteSourcePath, workDir, counting, ct);
                else if (target.Mode == ExecutionMode.DockerExec) await ExecInContainerAsync(containerId!, cmd, counting, log, ct);
                else await RunProcessAsync(cmd, counting, log, ct);
                rawBytes = counting.Written;
            }

            if (rawBytes == 0) throw new InvalidOperationException("The dump produced no output");

            File.Move(tempPath, finalPath, overwrite: true);
            var info = new FileInfo(finalPath);
            run.FilePath = Path.Combine(folderRel, fileName).Replace('\\', '/');
            run.SizeBytes = info.Length;
            run.Sha256 = await HashAsync(finalPath, CancellationToken.None);
            run.Slot = slot;

            // A rolling slot overwrote an older run's file
            await db.Runs.Where(r => r.TargetId == targetId && r.Id != runId && r.FilePath == run.FilePath)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.FileGone, true), CancellationToken.None);

            if (slot != null) target.NextSlot = (slot.Value + 1) % Math.Max(1, target.Keep);

            var pruned = BackupFiles.FilesToPrune(target, Directory.EnumerateFiles(folder).Select(Path.GetFileName)!, fileName);
            foreach (var p in pruned)
            {
                try { File.Delete(Path.Combine(folder, p)); log.AppendLine($"retention: deleted {p}"); }
                catch (Exception e) { log.AppendLine($"retention: could not delete {p}: {e.Message}"); }
            }
            if (pruned.Count > 0)
            {
                var gone = pruned.Select(p => Path.Combine(folderRel, p).Replace('\\', '/')).ToList();
                await db.Runs.Where(r => r.TargetId == targetId && r.FilePath != null && gone.Contains(r.FilePath))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.FileGone, true), CancellationToken.None);
            }

            run.Status = RunStatus.Succeeded;
        }
        catch (Exception e)
        {
            TryDelete(tempPath);
            run.Status = cancel.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Failed;
            run.Error = cancel.IsCancellationRequested ? "Cancelled"
                : e is OperationCanceledException ? $"Timed out after {target.TimeoutMinutes} min"
                : e.Message;
            if (e is not InvalidOperationException and not OperationCanceledException) _log.LogWarning(e, "Backup of {Target} failed", target.Name);
        }
        finally
        {
            if (cmd != null)
            {
                foreach (var f in cmd.TempFiles.Keys) TryDelete(f);
                if (cmd.OutputFile != null) TryDelete(cmd.OutputFile);
            }
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Log = Truncate(log.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        await PruneHistoryAsync(db, targetId);
        await _notifier.NotifyAsync(target, run);
    }

    /// <summary>Credentials: explicit values win; DockerExec fills blanks from the container's environment.</summary>
    public async Task<(Credentials, string?)> ResolveAsync(BackupTarget t, StringBuilder log, CancellationToken ct)
    {
        var user = t.Username;
        var password = Unprotect(t.PasswordProtected);
        string? containerId = null;

        if (t.Mode == ExecutionMode.DockerExec)
        {
            if (!_docker.Configured) throw new InvalidOperationException("Docker socket is not available — mount /var/run/docker.sock to use exec mode");
            if (string.IsNullOrWhiteSpace(t.ContainerName)) throw new InvalidOperationException("No container configured");
            var c = await _docker.InspectAsync(t.ContainerName, ct) ?? throw new InvalidOperationException($"Container '{t.ContainerName}' not found");
            if (!c.Running) throw new InvalidOperationException($"Container '{t.ContainerName}' is not running");
            containerId = c.Id;

            if (!string.IsNullOrWhiteSpace(t.PasswordEnv))
            {
                if (c.Env.TryGetValue(t.PasswordEnv, out var p)) password = p;
                else log.AppendLine($"warning: container has no {t.PasswordEnv} variable");
            }
            var detected = EngineCatalog.DetectCredentials(t.Engine, c.Env);
            if (string.IsNullOrEmpty(user) && detected.User != null) { user = detected.User; log.AppendLine($"user from container env: {user}"); }
            if (string.IsNullOrEmpty(password) && detected.Password != null) { password = detected.Password; log.AppendLine("password from container env"); }
        }
        return (new Credentials(user, password, string.IsNullOrWhiteSpace(t.DatabaseName) ? null : t.DatabaseName.Trim()), containerId);
    }

    private static Stream Compress(Stream file, Compression c) => c switch
    {
        Compression.Gzip => new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true),
        Compression.Brotli => new BrotliStream(file, CompressionLevel.Optimal, leaveOpen: true),
        _ => new NonClosingStream(file),
    };

    private async Task RunProcessAsync(DumpCommand cmd, Stream output, StringBuilder log, CancellationToken ct)
    {
        foreach (var (path, content) in cmd.TempFiles)
        {
            await File.WriteAllTextAsync(path, content, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var psi = new ProcessStartInfo(cmd.Argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in cmd.Argv.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in cmd.Env) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"Could not start {cmd.Argv[0]}: {e.Message}"); }

        var stderrTask = Task.Run(async () =>
        {
            var buf = new char[4096];
            int n;
            while ((n = await proc.StandardError.ReadAsync(buf, CancellationToken.None)) > 0)
                lock (log) { if (log.Length < 64_000) log.Append(buf, 0, n); }
        }, CancellationToken.None);

        try
        {
            if (cmd.OutputFile == null) await proc.StandardOutput.BaseStream.CopyToAsync(output, ct);
            else await proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        await stderrTask;

        if (proc.ExitCode != 0) throw new InvalidOperationException($"{cmd.Argv[0]} exited with code {proc.ExitCode}: {LastLine(log)}");

        if (cmd.OutputFile != null)
        {
            await using var f = File.OpenRead(cmd.OutputFile);
            await f.CopyToAsync(output, ct);
        }
    }

    private async Task ExecInContainerAsync(string containerId, DumpCommand cmd, Stream output, StringBuilder log, CancellationToken ct)
    {
        var stderr = new StringBuilder();
        var code = await _docker.ExecAsync(containerId, cmd.Argv, cmd.Env, output, stderr, ct);
        log.Append(stderr);
        if (code != 0) throw new InvalidOperationException($"{ExecToolName(cmd)} exited with code {code}: {LastLine(stderr)}");
    }

    private static string ExecToolName(DumpCommand cmd) => cmd.Argv[0] == "sh" ? "dump" : cmd.Argv[0];

    private static async Task SqliteBackupAsync(string source, string workDir, Stream output, CancellationToken ct)
    {
        if (!File.Exists(source)) throw new InvalidOperationException($"SQLite file not found: {source} (mount it into the ACopy container)");
        var tmp = Path.Combine(workDir, $"sqlite-{Guid.NewGuid():N}.db");
        try
        {
            await Task.Run(() =>
            {
                using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString());
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
            }, ct);
            await using var f = File.OpenRead(tmp);
            await f.CopyToAsync(output, ct);
        }
        finally { TryDelete(tmp); }
    }

    private async Task FinishAsync(long runId, RunStatus status, string error, string? log)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.Runs.Include(r => r.Target).FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null || run.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled) return;
        run.Status = status;
        run.Error = error;
        run.Log = log;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (run.Target != null && status == RunStatus.Failed) await _notifier.NotifyAsync(run.Target, run);
    }

    private async Task PruneHistoryAsync(AppDbContext db, int targetId)
    {
        var keep = Math.Max(10, _opt.HistoryPerTarget);
        var cutoff = await db.Runs.Where(r => r.TargetId == targetId).OrderByDescending(r => r.Id).Skip(keep).Select(r => (long?)r.Id).FirstOrDefaultAsync();
        if (cutoff != null) await db.Runs.Where(r => r.TargetId == targetId && r.Id <= cutoff).ExecuteDeleteAsync();
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }

    private string? Truncate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Length <= _opt.LogMaxChars ? s : "…" + s[^_opt.LogMaxChars..];
    }

    private static string LastLine(StringBuilder sb)
    {
        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : lines[^1];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>On start-up: runs left Queued/Running by a crash are failed; orphaned temp files removed.</summary>
    public async Task RecoverAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Runs.Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatus.Failed)
                .SetProperty(r => r.Error, "Interrupted by an ACopy restart")
                .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow));

        if (!Directory.Exists(_paths.Backups)) return;
        foreach (var f in Directory.EnumerateFiles(_paths.Backups, BackupFiles.TempPrefix + "*", SearchOption.AllDirectories))
            if (BackupFiles.IsAcopyTemp(Path.GetFileName(f))) TryDelete(f);
    }
}

/// <summary>Counts bytes written through it (the uncompressed dump size).</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long Written { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Written += count; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        Written += buffer.Length;
    }
}

/// <summary>Pass-through used for "no compression" so disposing it doesn't close the file early.</summary>
internal sealed class NonClosingStream(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
}
