using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ACopy.Api.Data;
using ACopy.Api.Destinations;
using ACopy.Api.Docker;
using ACopy.Api.Engines;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Services;

/// <param name="SafetyBackup">Take a fresh backup of the current state before restoring.</param>
/// <param name="AgeIdentity">age private key, when the key in ACopy has none stored.</param>
/// <param name="FromUploadId">Restore from this remote copy instead of the local file.</param>
public record RestoreOptions(bool SafetyBackup, string? AgeIdentity, long? FromUploadId);

/// <summary>
/// Executes backups and restores. A backup: resolve credentials → run the dump (locally or via docker exec) →
/// compress → encrypt → Syncthing-invisible temp file → atomic rename → local retention → copy to destinations →
/// remote retention → record → notify. A restore runs the pipeline backwards into the database.
/// One run per target at a time; at most <see cref="BackupOptions.MaxParallel"/> runs overall.
/// </summary>
public class BackupRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DockerClient _docker;
    private readonly IDataProtector _protector;
    private readonly Secrets _secrets;
    private readonly SettingsStore _settings;
    private readonly Notifier _notifier;
    private readonly DestinationService _destinations;
    private readonly PathsOptions _paths;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupRunner> _log;
    private readonly SemaphoreSlim _parallel;
    private readonly ConcurrentDictionary<int, (long RunId, CancellationTokenSource Cts)> _active = new();

    public BackupRunner(
        IServiceScopeFactory scopes, DockerClient docker, IDataProtectionProvider dp, Secrets secrets, SettingsStore settings,
        Notifier notifier, DestinationService destinations, IOptions<PathsOptions> paths, IOptions<BackupOptions> opt, ILogger<BackupRunner> log)
    {
        _scopes = scopes;
        _docker = docker;
        _protector = dp.CreateProtector("ACopy.TargetPassword");
        _secrets = secrets;
        _settings = settings;
        _notifier = notifier;
        _destinations = destinations;
        _paths = paths.Value;
        _opt = opt.Value;
        _log = log;
        _parallel = new SemaphoreSlim(Math.Max(1, _opt.MaxParallel));
    }

    public string Protect(string plain) => _protector.Protect(plain);
    public string? Unprotect(string? cipher) => string.IsNullOrEmpty(cipher) ? null : _protector.Unprotect(cipher);

    public bool IsActive(int targetId) => _active.ContainsKey(targetId);

    public bool Cancel(int targetId)
    {
        if (!_active.TryGetValue(targetId, out var a)) return false;
        a.Cts.Cancel();
        return true;
    }

    private static string WorkDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "acopy");
        Directory.CreateDirectory(d);
        return d;
    }

    // ════════════════════════════════════════════════════════════════
    //  Queueing
    // ════════════════════════════════════════════════════════════════

    /// <summary>Queue a backup. Returns the run id, or null when the target already has a run in flight.</summary>
    public Task<long?> StartAsync(int targetId, RunTrigger trigger) =>
        StartCoreAsync(targetId, new BackupRun { TargetId = targetId, Kind = RunKind.Backup, Trigger = trigger },
            (runId, ct) => RunBackupCoreAsync(targetId, runId, ct));

    /// <summary>Queue a restore of <paramref name="sourceRunId"/>'s file. Null when the target is busy.</summary>
    public async Task<long?> StartRestoreAsync(long sourceRunId, RestoreOptions options)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == sourceRunId) ?? throw new KeyNotFoundException();
        var targetId = source.TargetId;
        return await StartCoreAsync(targetId,
            new BackupRun { TargetId = targetId, Kind = RunKind.Restore, Trigger = RunTrigger.Manual, SourceRunId = sourceRunId, FilePath = source.FilePath },
            async (runId, ct) =>
            {
                if (options.SafetyBackup)
                {
                    long safetyId;
                    using (var s = _scopes.CreateScope())
                    {
                        var sdb = s.ServiceProvider.GetRequiredService<AppDbContext>();
                        var safety = new BackupRun { TargetId = targetId, Kind = RunKind.Backup, Trigger = RunTrigger.SafetyBackup, Status = RunStatus.Queued, StartedAt = DateTimeOffset.UtcNow };
                        sdb.Runs.Add(safety);
                        await sdb.SaveChangesAsync(CancellationToken.None);
                        safetyId = safety.Id;
                    }
                    await RunBackupCoreAsync(targetId, safetyId, ct);
                    using var s2 = _scopes.CreateScope();
                    var status = await s2.ServiceProvider.GetRequiredService<AppDbContext>().Runs.Where(r => r.Id == safetyId).Select(r => r.Status).FirstAsync(CancellationToken.None);
                    if (status != RunStatus.Succeeded)
                    {
                        await FinishAsync(runId, RunStatus.Failed, $"Safety backup (run #{safetyId}) did not succeed — nothing was restored", null);
                        return;
                    }
                }
                await RunRestoreCoreAsync(runId, options, ct);
            });
    }

    private async Task<long?> StartCoreAsync(int targetId, BackupRun run, Func<long, CancellationToken, Task> work)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Targets.AnyAsync(t => t.Id == targetId)) throw new KeyNotFoundException();

        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(targetId, (0, cts))) { cts.Dispose(); return null; }
        try
        {
            run.Status = RunStatus.Queued;
            run.StartedAt = DateTimeOffset.UtcNow;
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

        _ = Task.Run(() => ExecuteAsync(targetId, run.Id, cts, work));
        return run.Id;
    }

    private async Task ExecuteAsync(int targetId, long runId, CancellationTokenSource cts, Func<long, CancellationToken, Task> work)
    {
        try
        {
            await _parallel.WaitAsync(cts.Token);
            try { await work(runId, cts.Token); }
            finally { _parallel.Release(); }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(runId, RunStatus.Cancelled, "Cancelled before it started", null);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Run {RunId} crashed", runId);
            await FinishAsync(runId, RunStatus.Failed, e.Message, null);
        }
        finally
        {
            _active.TryRemove(targetId, out _);
            cts.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Backup
    // ════════════════════════════════════════════════════════════════

    private async Task RunBackupCoreAsync(int targetId, long runId, CancellationToken cancel)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var target = await db.Targets.Include(t => t.EncryptionKey).Include(t => t.Destinations).ThenInclude(d => d.Destination)
            .FirstAsync(t => t.Id == targetId);
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

        var key = target.EncryptionKey;
        var slot = target.Naming == NamingMode.Rolling ? Math.Clamp(target.NextSlot, 0, Math.Max(1, target.Keep) - 1) : (int?)null;
        var fileName = BackupFiles.FileName(target, localNow, slot ?? 0, key?.Kind);
        var finalPath = Path.Combine(folder, fileName);
        var tempPath = Path.Combine(folder, BackupFiles.TempName(fileName));
        var workDir = WorkDir();

        DumpCommand? cmd = null;
        var succeeded = false;
        try
        {
            var (creds, containerId) = await ResolveAsync(target, log, ct);
            cmd = DumpCommandBuilder.Build(target, creds, workDir);
            long rawBytes;

            AgeProcess? age = null;
            try
            {
                await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    Stream sink = file;
                    if (key?.Kind == KeyKind.Age)
                    {
                        // age writes the ciphertext itself; we stream compressed plaintext into its stdin
                        file.Close();
                        age = Encryption.StartAgeEncrypt(Encryption.ParseRecipients(key.Recipients), tempPath);
                        sink = new NonClosingStream(age.Input); // CompleteAsync closes stdin
                    }
                    else if (key?.Kind == KeyKind.Passphrase)
                    {
                        var pass = _secrets.Unprotect(key.SecretProtected) ?? throw new InvalidOperationException($"Key '{key.Name}' has no passphrase");
                        sink = Encryption.PassphraseEncrypt(file, pass);
                    }

                    await using (var encrypted = sink == file ? new NonClosingStream(file) : sink)
                    await using (var compressed = Compress(encrypted, target.Compression))
                    {
                        var counting = new CountingStream(compressed);
                        if (cmd.SqliteSourcePath != null) await SqliteBackupAsync(cmd.SqliteSourcePath, workDir, counting, ct);
                        else if (target.Mode == ExecutionMode.DockerExec) await ExecInContainerAsync(containerId!, cmd, counting, log, ct);
                        else await RunProcessAsync(cmd, counting, log, ct);
                        rawBytes = counting.Written;
                    }
                }
                if (age != null) await age.CompleteAsync(ct);
            }
            finally
            {
                if (age != null) await age.DisposeAsync();
            }

            if (rawBytes == 0) throw new InvalidOperationException("The dump produced no output");
            if (key != null) log.AppendLine($"encrypted with key '{key.Name}' ({key.Kind})");

            File.Move(tempPath, finalPath, overwrite: true);
            run.FilePath = Path.Combine(folderRel, fileName).Replace('\\', '/');
            run.SizeBytes = new FileInfo(finalPath).Length;
            run.Sha256 = await HashAsync(finalPath, CancellationToken.None);
            run.Slot = slot;
            run.EncryptionKeyId = key?.Id;

            // A rolling slot overwrote an older run's file
            await db.Runs.Where(r => r.TargetId == targetId && r.Id != runId && r.FilePath == run.FilePath && r.Kind == RunKind.Backup)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.FileGone, true), CancellationToken.None);

            if (slot != null) target.NextSlot = (slot.Value + 1) % Math.Max(1, target.Keep);

            var pruned = BackupFiles.FilesToPrune(target, BackupFiles.ListFolder(folder), fileName, DateTime.UtcNow);
            foreach (var p in pruned)
            {
                try { File.Delete(Path.Combine(folder, p)); log.AppendLine($"retention: deleted {p}"); }
                catch (Exception e) { log.AppendLine($"retention: could not delete {p}: {e.Message}"); }
            }
            if (pruned.Count > 0)
            {
                var gone = pruned.Select(p => Path.Combine(folderRel, p).Replace('\\', '/')).ToList();
                await db.Runs.Where(r => r.TargetId == targetId && r.FilePath != null && gone.Contains(r.FilePath) && r.Kind == RunKind.Backup)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.FileGone, true), CancellationToken.None);
            }

            run.Status = RunStatus.Succeeded;
            succeeded = true;
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
        await db.SaveChangesAsync(CancellationToken.None);

        if (succeeded) await UploadAsync(db, target, run, finalPath, folderRel, fileName, log, ct);

        run.Log = Truncate(log.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        await PruneHistoryAsync(db, targetId);
        await _notifier.NotifyAsync(target, run);
    }

    /// <summary>Copies the finished file to each enabled destination, then applies that destination's retention.</summary>
    private async Task UploadAsync(AppDbContext db, BackupTarget target, BackupRun run, string localPath, string folderRel, string fileName, StringBuilder log, CancellationToken ct)
    {
        foreach (var td in target.Destinations.Where(d => d.Destination is { Enabled: true }))
        {
            var d = td.Destination!;
            var upload = new RunUpload { RunId = run.Id, DestinationId = d.Id, Destination = d, Status = UploadStatus.Pending };
            run.Uploads.Add(upload);
            var sw = Stopwatch.StartNew();
            try
            {
                upload.RemoteRef = await _destinations.UploadAsync(d, target, run, localPath, folderRel, fileName, ct);
                upload.Status = UploadStatus.Succeeded;
                log.AppendLine($"{d.Name}: uploaded in {sw.Elapsed.TotalSeconds:0.#} s");
            }
            catch (Exception e)
            {
                upload.Status = UploadStatus.Failed;
                upload.Error = e is OperationCanceledException ? "Timed out" : e.Message;
                if (e is PartialUploadException partial) upload.RemoteRef = partial.RemoteRef;
                log.AppendLine($"{d.Name}: upload failed: {upload.Error}");
            }
            upload.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            if (upload.Status == UploadStatus.Succeeded)
            {
                try { await _destinations.PruneAsync(d, target, run, folderRel, fileName, db, log, ct); }
                catch (Exception e) { log.AppendLine($"{d.Name}: retention failed: {e.Message}"); }
            }
        }
    }

    /// <summary>Re-sends one failed (or deleted) upload of an existing local file.</summary>
    public async Task<RunUpload> RetryUploadAsync(long uploadId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await db.Uploads.Include(x => x.Destination).Include(x => x.Run).ThenInclude(r => r!.Target).FirstAsync(x => x.Id == uploadId, ct);
        var run = u.Run!;
        if (run.FilePath == null || run.FileGone) throw new InvalidOperationException("The local file is gone");
        var local = Path.Combine(_paths.Backups, run.FilePath);
        var folderRel = Path.GetDirectoryName(run.FilePath)!.Replace('\\', '/');
        try
        {
            // A partly sent Telegram upload only goes to the chats that are still missing it
            var previous = u.Deleted ? null : u.RemoteRef;
            u.RemoteRef = await _destinations.UploadAsync(u.Destination!, run.Target!, run, local, folderRel, Path.GetFileName(local), ct, previous);
            u.Status = UploadStatus.Succeeded;
            u.Error = null;
            u.Deleted = false;
        }
        catch (Exception e)
        {
            u.Status = UploadStatus.Failed;
            u.Error = e.Message;
            if (e is PartialUploadException partial) { u.RemoteRef = partial.RemoteRef; u.Deleted = false; }
        }
        u.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        return u;
    }

    // ════════════════════════════════════════════════════════════════
    //  Restore
    // ════════════════════════════════════════════════════════════════

    private async Task RunRestoreCoreAsync(long runId, RestoreOptions options, CancellationToken cancel)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.Runs.FirstAsync(r => r.Id == runId);
        var source = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == run.SourceRunId);
        var target = await db.Targets.AsNoTracking().FirstAsync(t => t.Id == run.TargetId);
        run.Status = RunStatus.Running;
        await db.SaveChangesAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, target.TimeoutMinutes) * 2));
        var ct = timeout.Token;

        var log = new StringBuilder();
        var workDir = WorkDir();
        var scratch = new List<string>();
        string? containerFile = null;
        string? containerId = null;
        RestoreCommand? cmd = null;
        try
        {
            if (RestoreCommandBuilder.UnsupportedReason(target) is { } reason) throw new InvalidOperationException(reason);

            // 1. Get the file — local copy, or download a remote one
            string sourceFile;
            var local = source.FilePath == null ? null : Path.Combine(_paths.Backups, source.FilePath);
            if (options.FromUploadId is { } uploadId)
            {
                var upload = await db.Uploads.Include(u => u.Destination).FirstAsync(u => u.Id == uploadId && u.RunId == source.Id, ct);
                sourceFile = Path.Combine(workDir, $"restore-{runId}-download{Path.GetExtension(source.FilePath)}");
                scratch.Add(sourceFile);
                log.AppendLine($"downloading from {upload.Destination!.Name}");
                await _destinations.DownloadAsync(upload.Destination, upload.RemoteRef!, sourceFile, ct);
            }
            else if (local != null && File.Exists(local) && !source.FileGone) sourceFile = local;
            else throw new InvalidOperationException("The backup file is no longer on disk — pick a remote copy");

            // 2. Decrypt + decompress into a plain file
            var plain = Path.Combine(workDir, $"restore-{runId}.plain");
            scratch.Add(plain);
            await PreparePlainAsync(db, source, sourceFile, plain, options, workDir, scratch, log, ct);
            log.AppendLine($"prepared {Notifier.FormatSize(new FileInfo(plain).Length)} of dump data");

            // 3. Load it
            var (creds, cid) = await ResolveAsync(target, log, ct);
            containerId = cid;
            if (target.Mode == ExecutionMode.DockerExec)
            {
                var name = $"acopy-restore-{runId}";
                await _docker.PutFileAsync(containerId!, plain, "/tmp", name, ct);
                containerFile = $"/tmp/{name}";
                cmd = RestoreCommandBuilder.Build(target, creds, containerFile, workDir);
                var stderr = new StringBuilder();
                using var stdout = new MemoryStream();
                var code = await _docker.ExecAsync(containerId!, cmd.Argv, cmd.Env, stdout, stderr, ct);
                AppendOutput(log, stdout, stderr);
                if (code != 0) throw new InvalidOperationException($"restore exited with code {code}: {LastLine(stderr)}");
            }
            else
            {
                cmd = RestoreCommandBuilder.Build(target, creds, plain, workDir);
                if (cmd.SqliteTargetPath != null) await SqliteRestoreAsync(plain, cmd.SqliteTargetPath, ct);
                else await RunRestoreProcessAsync(cmd, log, ct);
            }

            run.Status = RunStatus.Succeeded;
            log.AppendLine($"restored {source.FilePath} (run #{source.Id})");
        }
        catch (Exception e)
        {
            run.Status = cancel.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Failed;
            run.Error = cancel.IsCancellationRequested ? "Cancelled" : e is OperationCanceledException ? "Timed out" : e.Message;
            if (e is not InvalidOperationException and not OperationCanceledException) _log.LogWarning(e, "Restore of {Target} failed", target.Name);
        }
        finally
        {
            foreach (var f in scratch) TryDelete(f);
            if (cmd != null) foreach (var f in cmd.TempFiles.Keys) TryDelete(f);
            if (containerFile != null && containerId != null)
            {
                try { await _docker.ExecAsync(containerId, ["rm", "-f", containerFile], new Dictionary<string, string>(), Stream.Null, new StringBuilder(), CancellationToken.None); }
                catch { /* best effort */ }
            }
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Log = Truncate(log.ToString());
        await db.SaveChangesAsync(CancellationToken.None);
        await _notifier.NotifyAsync(target, run);
    }

    private async Task PreparePlainAsync(
        AppDbContext db, BackupRun source, string sourceFile, string plain, RestoreOptions options,
        string workDir, List<string> scratch, StringBuilder log, CancellationToken ct)
    {
        var name = source.FilePath ?? sourceFile;
        var current = sourceFile;
        var key = source.EncryptionKeyId == null ? null : await db.Keys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == source.EncryptionKeyId, ct);

        Stream OpenDecrypted()
        {
            if (name.EndsWith(".enc", StringComparison.Ordinal))
            {
                if (key == null) throw new InvalidOperationException("The file is encrypted but its key no longer exists in ACopy");
                var pass = _secrets.Unprotect(key.SecretProtected) ?? throw new InvalidOperationException($"Key '{key.Name}' has no passphrase");
                log.AppendLine($"decrypting with key '{key.Name}'");
                return Encryption.PassphraseDecrypt(File.OpenRead(current), pass);
            }
            return File.OpenRead(current);
        }

        if (name.EndsWith(".age", StringComparison.Ordinal))
        {
            var identity = !string.IsNullOrWhiteSpace(options.AgeIdentity) ? options.AgeIdentity
                : key != null ? _secrets.Unprotect(key.SecretProtected) : null;
            if (string.IsNullOrWhiteSpace(identity))
                throw new InvalidOperationException("This file is age-encrypted and ACopy has no private key for it — paste the identity to restore");
            var decrypted = Path.Combine(workDir, $"{Path.GetFileName(plain)}.dec");
            scratch.Add(decrypted);
            log.AppendLine($"decrypting with age{(key != null ? $" (key '{key.Name}')" : "")}");
            await Encryption.AgeDecryptAsync(identity, current, decrypted, workDir, ct);
            current = decrypted;
            name = name[..^4];
        }

        var plainName = name.EndsWith(".enc", StringComparison.Ordinal) ? name[..^4] : name;
        await using var input = OpenDecrypted();
        await using Stream decompressed = plainName.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(input, CompressionMode.Decompress)
            : plainName.EndsWith(".br", StringComparison.Ordinal) ? new BrotliStream(input, CompressionMode.Decompress)
            : input;
        await using var output = File.Create(plain);
        await decompressed.CopyToAsync(output, ct);
    }

    private static async Task RunRestoreProcessAsync(RestoreCommand cmd, StringBuilder log, CancellationToken ct)
    {
        foreach (var (path, content) in cmd.TempFiles)
        {
            await File.WriteAllTextAsync(path, content, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var psi = new ProcessStartInfo(cmd.Argv[0])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = cmd.StdinFile != null, UseShellExecute = false,
        };
        foreach (var a in cmd.Argv.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in cmd.Env) psi.Environment[k] = v;
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"Could not start {cmd.Argv[0]}: {e.Message}"); }

        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (cmd.StdinFile != null)
            {
                await using (var f = File.OpenRead(cmd.StdinFile)) await f.CopyToAsync(proc.StandardInput.BaseStream, ct);
                proc.StandardInput.Close();
            }
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw;
        }
        var err = await stderr;
        var o = await stdout;
        if (o.Length > 0) log.AppendLine(o.Length > 8000 ? o[^8000..] : o);
        if (err.Length > 0) log.AppendLine(err.Length > 16000 ? err[^16000..] : err);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{cmd.Argv[0]} exited with code {proc.ExitCode}: {LastLine(new StringBuilder(err))}");
    }

    private static async Task SqliteRestoreAsync(string plain, string targetPath, CancellationToken ct)
    {
        if (!File.Exists(targetPath)) throw new InvalidOperationException($"SQLite file not found: {targetPath}");
        await Task.Run(() =>
        {
            using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = plain, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetPath, Pooling = false }.ToString());
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
        }, ct);
    }

    private static void AppendOutput(StringBuilder log, MemoryStream stdout, StringBuilder stderr)
    {
        var o = Encoding.UTF8.GetString(stdout.ToArray());
        if (o.Length > 0) log.AppendLine(o.Length > 8000 ? o[^8000..] : o);
        if (stderr.Length > 0) log.Append(stderr);
    }

    // ════════════════════════════════════════════════════════════════
    //  Shared
    // ════════════════════════════════════════════════════════════════

    /// <summary>Credentials: explicit values win; DockerExec fills blanks from the container's environment.</summary>
    public async Task<(Credentials, string?)> ResolveAsync(BackupTarget t, StringBuilder log, CancellationToken ct)
    {
        var user = t.Username;
        var password = Unprotect(t.PasswordProtected);
        string? containerId = null;

        if (t.Mode == ExecutionMode.DockerExec)
        {
            if (!_docker.Configured) throw new InvalidOperationException("Docker is not available — mount /var/run/docker.sock or set DOCKER_HOST to use exec mode");
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

    private static Stream Compress(Stream output, Compression c) => c switch
    {
        Compression.Gzip => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
        Compression.Brotli => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
        _ => new NonClosingStream(output),
    };

    private static async Task RunProcessAsync(DumpCommand cmd, Stream output, StringBuilder log, CancellationToken ct)
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
        if (code != 0) throw new InvalidOperationException($"{(cmd.Argv[0] == "sh" ? "dump" : cmd.Argv[0])} exited with code {code}: {LastLine(stderr)}");
    }

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
        // Never drop the history row of a file that still exists — it is how the UI restores and downloads it
        if (cutoff != null) await db.Runs.Where(r => r.TargetId == targetId && r.Id <= cutoff && (r.FileGone || r.FilePath == null || r.Kind == RunKind.Restore)).ExecuteDeleteAsync();
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
        await db.Uploads.Where(u => u.Status == UploadStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UploadStatus.Failed).SetProperty(u => u.Error, "Interrupted by an ACopy restart"));

        var work = Path.Combine(Path.GetTempPath(), "acopy");
        if (Directory.Exists(work)) foreach (var f in Directory.EnumerateFiles(work)) TryDelete(f);
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

/// <summary>Pass-through used where disposing must not close the underlying stream.</summary>
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
