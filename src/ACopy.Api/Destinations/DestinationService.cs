using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ACopy.Api.Data;
using ACopy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace ACopy.Api.Destinations;

public record DestinationTestResult(bool Ok, string Message);

/// <summary>Copies finished backups to destinations (rclone backends, Telegram) and applies remote retention.</summary>
public class DestinationService(Secrets secrets, TelegramClient telegram, SettingsStore settings, Microsoft.Extensions.Options.IOptions<BackupOptions> backup)
{
    private const string RemoteName = "ACOPY";

    // ── Upload ──

    /// <summary>Uploads one backup file; returns the remote reference (path, or Telegram chats and message ids).</summary>
    /// <param name="previousRef">Telegram retry: the reference of the earlier attempt — chats that already got the file are skipped.</param>
    /// <exception cref="PartialUploadException">Telegram: some chats got the file, others did not.</exception>
    public async Task<string> UploadAsync(Destination d, BackupTarget t, BackupRun run, string localPath, string folderRel, string fileName,
        CancellationToken ct, string? previousRef = null)
    {
        var (remoteName, _) = await RemoteNameAsync(d, t, run, fileName);
        if (d.Kind == DestinationKind.Telegram) return await UploadTelegramAsync(d, t, run, localPath, folderRel, remoteName, previousRef, ct);

        var remote = Join(Join(RemoteBase(d), folderRel), remoteName);
        var env = await RcloneEnvAsync(d, ct);
        var (code, _, err) = await RunRcloneAsync(["copyto", localPath, remote, "--retries", "3", "--low-level-retries", "10"], env, ct);
        if (code != 0) throw new InvalidOperationException($"rclone copyto failed: {LastLine(err)}");
        return remote;
    }

    /// <summary>
    /// Sends the file (split into parts above the size limit) to every chat of the job. Each part is uploaded once and
    /// sent to the other chats by its file id. A chat that fails is reported; the others keep their copy.
    /// With a shared upload folder the parts are copied there (kept until retention removes them) and sent by path.
    /// </summary>
    private async Task<string> UploadTelegramAsync(Destination d, BackupTarget t, BackupRun run, string localPath, string folderRel, string fileName,
        string? previousRef, CancellationToken ct)
    {
        var (bot, chats) = TelegramOf(d, t);
        var p = Params(d);
        var defaultLimit = TelegramClient.IsPublic(bot.ApiUrl) ? 49 : 1990;
        var partMb = p.TryGetValue("maxPartMb", out var m) && int.TryParse(m, out var mb) && mb > 0 ? mb : defaultLimit;
        var partBytes = (long)partMb * 1024 * 1024;

        var done = TelegramRef.Parse(previousRef, LegacyChat(d));
        var pending = chats.Where(c => !done.ContainsKey(c)).ToList();
        var sent = pending.ToDictionary(c => c, _ => new List<long>(), StringComparer.Ordinal);
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);

        var size = new FileInfo(localPath).Length;
        var parts = (int)Math.Max(1, (size + partBytes - 1) / partBytes);
        await using var file = File.OpenRead(localPath);

        var staging = StagingOf(bot.ApiUrl, folderRel);
        if (staging is { } st && pending.Count > 0)
        {
            Directory.CreateDirectory(st.LocalDir);
            // An earlier copy under this name (a rolling slot) may have had more parts than now
            var names = Enumerable.Range(1, parts).Select(n => parts == 1 ? fileName : $"{fileName}.{n:000}").ToHashSet();
            foreach (var old in BackupFiles.FileAndParts(fileName, Directory.EnumerateFiles(st.LocalDir).Select(Path.GetFileName).OfType<string>()))
                if (!names.Contains(old)) File.Delete(Path.Combine(st.LocalDir, old));
        }

        for (var i = 0; i < parts && pending.Count > failed.Count; i++)
        {
            var offset = i * partBytes;
            var len = Math.Min(partBytes, size - offset);
            var name = parts == 1 ? fileName : $"{fileName}.{i + 1:000}";
            var caption = new StringBuilder()
                .Append($"🗄 <b>{TelegramClient.Escape(t.Name)}</b>\n")
                .Append($"<code>{TelegramClient.Escape(fileName)}</code> · {Notifier.FormatSize(size)}");
            if (parts > 1) caption.Append($"\npart {i + 1}/{parts} — join with <code>cat {TelegramClient.Escape(fileName)}.* &gt; {TelegramClient.Escape(fileName)}</code>");
            if (i == 0 && run.Sha256 != null) caption.Append($"\nsha256 <code>{run.Sha256}</code>");

            string? fileId = null;
            var staged = false;
            foreach (var chat in pending.Where(c => !failed.ContainsKey(c)))
            {
                try
                {
                    SentDocument msg;
                    if (fileId != null) msg = await telegram.SendDocumentByIdAsync(bot.ForChat(chat), fileId, caption.ToString(), ct);
                    else if (staging is { } sh)
                    {
                        // One copy per database: another destination may already have written this part in this run
                        var target = new FileInfo(Path.Combine(sh.LocalDir, name));
                        if (!staged && !(target.Exists && target.Length == len && target.LastWriteTimeUtc >= run.StartedAt.UtcDateTime))
                        {
                            // Written under a temp name and renamed, so the folder only ever holds complete copies
                            var tmp = Path.Combine(sh.LocalDir, BackupFiles.TempName(name));
                            await using (var part = new SubStream(file, offset, len))
                            await using (var copy = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                            await using (var synced = new SyncingStream(copy, tmp, (long)Math.Max(0, backup.Value.SyncEveryMb) * 1024 * 1024))
                                await part.CopyToAsync(synced, ct);
                            File.Move(tmp, target.FullName, overwrite: true);
                        }
                        staged = true;
                        try { msg = await telegram.SendDocumentByPathAsync(bot.ForChat(chat), $"{sh.ServerDir}/{name}", caption.ToString(), ct); }
                        catch (InvalidOperationException e) when (e.Message.Contains("file", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"{e.Message} — is {sh.LocalDir} the same folder as {sh.ServerDir} in the Bot API server?");
                        }
                        fileId = msg.FileId;
                    }
                    else
                    {
                        await using var part = new SubStream(file, offset, len);
                        msg = await telegram.SendDocumentAsync(bot.ForChat(chat), part, name, caption.ToString(), ct);
                        fileId = msg.FileId;
                    }
                    sent[chat].Add(msg.MessageId);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed[chat] = e.Message;
                    // An incomplete set of parts is useless — take back what this chat already got of this upload
                    foreach (var id in sent[chat])
                        try { await telegram.DeleteMessageAsync(bot.ForChat(chat), id, ct); } catch (Exception) { /* best effort */ }
                    sent[chat].Clear();
                }
            }
        }

        foreach (var (chat, ids) in sent) if (ids.Count > 0) done[chat] = ids;
        var remoteRef = TelegramRef.Format(done);
        if (failed.Count == 0) return remoteRef;
        var error = string.Join("; ", failed.Select(f => $"chat {f.Key}: {f.Value}"));
        if (done.Count == 0) throw new InvalidOperationException(error);
        throw new PartialUploadException(remoteRef, $"sent to {done.Count} of {done.Count + failed.Count} chats — {error}");
    }

    /// <summary>
    /// A database's folder in the shared upload folder (ACopy's path, the Bot API server's path) when files for this
    /// server go by path: <c>&lt;upload folder&gt;/&lt;database folder&gt;</c> — one copy for every chat and destination.
    /// </summary>
    private (string LocalDir, string ServerDir)? StagingOf(string apiUrl, string folderRel) =>
        telegram.StagingFor(apiUrl) is { } s ? (Path.Combine(s.Local, folderRel), $"{s.Server}/{folderRel}") : null;

    /// <summary>The chat of an upload recorded before multi-chat support (the reference then held message ids only).</summary>
    private string? LegacyChat(Destination d) => TelegramClient.ParseChats(Params(d).GetValueOrDefault("chatId")).FirstOrDefault();

    /// <summary>
    /// The name of a backup at a destination, and the naming its copies there follow. Telegram always gets the
    /// timestamped name (the date of the backup); rclone destinations keep the local name unless set to
    /// <c>fileNames=timestamp</c>.
    /// </summary>
    public async Task<(string Name, NamingMode Naming)> RemoteNameAsync(Destination d, BackupTarget t, BackupRun run, string localName)
    {
        var timestamped = d.Kind == DestinationKind.Telegram || Params(d).GetValueOrDefault("fileNames") == "timestamp";
        if (!timestamped || t.Naming == NamingMode.Timestamp) return (localName, t.Naming);
        var localTime = TimeZoneInfo.ConvertTime(run.StartedAt, await settings.GetTimeZoneAsync()).DateTime;
        return (BackupFiles.TimestampedName(t, localName, localTime), NamingMode.Timestamp);
    }

    // ── Remote retention ──

    public async Task PruneAsync(Destination d, BackupTarget t, BackupRun run, string folderRel, string localName, AppDbContext db, StringBuilder runLog, CancellationToken ct)
    {
        var (justWritten, naming) = await RemoteNameAsync(d, t, run, localName);
        // Telegram messages are never deleted: once sent, a backup stays in the chats until someone removes it there.
        // The copies in the shared upload folder follow the database's own retention.
        if (d.Kind == DestinationKind.Telegram)
        {
            if (StagingOf(TelegramOf(d, t).Bot.ApiUrl, folderRel) is { } st) PruneStaging(t, naming, st.LocalDir, justWritten, runLog);
            return;
        }
        var policy = new RetentionPolicy(d.Keep ?? t.Keep, d.Keep != null || d.MaxAgeDays != null ? d.MaxAgeDays : t.MaxAgeDays);

        var dir = Join(RemoteBase(d), folderRel);
        var env = await RcloneEnvAsync(d, ct);
        var (code, stdout, err) = await RunRcloneAsync(["lsjson", dir, "--files-only"], env, ct);
        if (code != 0) { runLog.AppendLine($"{d.Name}: listing for retention failed: {LastLine(err)}"); return; }
        var entries = JsonSerializer.Deserialize<List<LsEntry>>(stdout, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var files = entries.Select(e => new FileEntry(e.Name, e.ModTime.UtcDateTime));
        var prune = BackupFiles.FilesToPrune(BackupFiles.BaseName(t), naming, policy, files, justWritten, DateTime.UtcNow);
        foreach (var name in prune)
        {
            var path = Join(dir, name);
            var (dc, _, de) = await RunRcloneAsync(["deletefile", path], env, ct);
            if (dc == 0)
            {
                runLog.AppendLine($"{d.Name}: retention deleted {name}");
                await db.Uploads.Where(u => u.DestinationId == d.Id && u.RemoteRef == path)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.Deleted, true), ct);
            }
            else runLog.AppendLine($"{d.Name}: could not delete {name}: {LastLine(de)}");
        }
    }

    /// <summary>Deletes copies beyond the database's retention from its upload folder — only names it produces; parts go together.</summary>
    private static void PruneStaging(BackupTarget t, NamingMode naming, string dir, string justWritten, StringBuilder runLog)
    {
        var prune = BackupFiles.PartedFilesToPrune(BackupFiles.BaseName(t), naming, new RetentionPolicy(Math.Max(1, t.Keep), t.MaxAgeDays),
            BackupFiles.ListFolder(dir), justWritten, DateTime.UtcNow);
        foreach (var name in prune)
        {
            try { File.Delete(Path.Combine(dir, name)); runLog.AppendLine($"upload folder: retention deleted {name}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { runLog.AppendLine($"upload folder: could not delete {name}: {e.Message}"); }
        }
    }

    // ── Download (restore from a remote copy) ──

    public bool CanDownload(Destination d) => d.Kind == DestinationKind.Rclone;

    public async Task DownloadAsync(Destination d, string remoteRef, string localPath, CancellationToken ct)
    {
        if (!CanDownload(d)) throw new InvalidOperationException($"Restoring from {d.Kind} is not supported");
        var env = await RcloneEnvAsync(d, ct);
        var (code, _, err) = await RunRcloneAsync(["copyto", remoteRef, localPath], env, ct);
        if (code != 0) throw new InvalidOperationException($"rclone download failed: {LastLine(err)}");
    }

    // ── Test ──

    public async Task<DestinationTestResult> TestAsync(Destination d, CancellationToken ct)
    {
        try
        {
            if (d.Kind == DestinationKind.Telegram)
            {
                var (tg, chats) = TelegramOf(d, null);
                var bot = await telegram.GetMeAsync(tg, ct);
                var failures = new List<string>();
                foreach (var chat in chats)
                {
                    try { await telegram.SendMessageAsync(tg.ForChat(chat), $"✅ ACopy destination <b>{TelegramClient.Escape(d.Name)}</b> works.", ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { failures.Add($"chat {chat}: {e.Message}"); }
                }
                var server = TelegramClient.IsPublic(tg.ApiUrl) ? "" : $" via {tg.ApiUrl}";
                return failures.Count == 0
                    ? new(true, $"@{bot}{server} posted a test message to chat{(chats.Count == 1 ? "" : "s")} {string.Join(", ", chats)}")
                    : new(false, $"@{bot}{server}: {string.Join("; ", failures)}");
            }
            var env = await RcloneEnvAsync(d, ct);
            var basePath = RemoteBase(d);
            var (mc, _, me) = await RunRcloneAsync(["mkdir", basePath], env, ct);
            if (mc != 0) return new(false, LastLine(me));
            var (lc, lo, le) = await RunRcloneAsync(["lsjson", basePath, "--max-depth", "1"], env, ct);
            if (lc != 0) return new(false, LastLine(le));
            var n = JsonSerializer.Deserialize<List<LsEntry>>(lo, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Count ?? 0;
            return new(true, $"Connected — {basePath} has {n} entr{(n == 1 ? "y" : "ies")}");
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    // ── Helpers ──

    public Dictionary<string, string> Params(Destination d) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(d.ParamsJson) ?? [];

    /// <summary>
    /// The bot of a Telegram destination and the chats to send to: the job's own chat list when it has one, otherwise
    /// the destination's. The forum topic only applies to the destination's chats.
    /// </summary>
    public (TelegramTarget Bot, List<string> Chats) TelegramOf(Destination d, BackupTarget? job, bool requireChat = true)
    {
        var p = Params(d);
        var s = secrets.UnprotectMap(d.SecretsProtected);
        if (!s.TryGetValue("botToken", out var token) || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Telegram bot token is missing");
        var jobChats = TelegramClient.ParseChats(job?.TelegramChatIds);
        var chats = jobChats.Count > 0 ? jobChats : TelegramClient.ParseChats(p.GetValueOrDefault("chatId"));
        if (requireChat && chats.Count == 0) throw new InvalidOperationException("Telegram chat id is missing");
        int? thread = jobChats.Count == 0 && p.TryGetValue("threadId", out var th) && int.TryParse(th, out var ti) ? ti : null;
        var api = telegram.ResolveApiUrl(p.GetValueOrDefault("apiUrl"));
        return (new TelegramTarget(token.Trim(), chats.FirstOrDefault() ?? "", thread, api), chats);
    }

    /// <summary>The one-time logOut from api.telegram.org before the destination's bot moves to a self-hosted server.</summary>
    public async Task<DestinationTestResult> LogOutFromPublicAsync(Destination d, CancellationToken ct)
    {
        try
        {
            var (bot, _) = TelegramOf(d, null, requireChat: false);
            await telegram.LogOutFromPublicAsync(bot.Token, ct);
            return new(true, "Logged out from api.telegram.org — the bot can now use a self-hosted Bot API server (it cannot log back in to the public one for 10 minutes)");
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new(false, e.Message); }
    }

    public static string RemoteBase(Destination d)
    {
        var path = (d.RemotePath ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        return $"{RemoteName}:{path}";
    }

    public static string Join(string a, string b) => a.EndsWith(':') || a.EndsWith('/') ? a + b : $"{a}/{b}";

    /// <summary>Configures the remote through RCLONE_CONFIG_ACOPY_* variables — no config file on disk.</summary>
    internal async Task<Dictionary<string, string>> RcloneEnvAsync(Destination d, CancellationToken ct)
    {
        var p = Params(d);
        var s = secrets.UnprotectMap(d.SecretsProtected);
        var env = new Dictionary<string, string>();
        void Set(string key, string value) => env[$"RCLONE_CONFIG_{RemoteName}_{key.ToUpperInvariant()}"] = value;

        if (d.RcloneType == "other")
        {
            if (!p.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type)) throw new InvalidOperationException("rclone type is missing");
            Set("type", type.Trim());
            foreach (var (k, v) in ParseOptionLines(p.GetValueOrDefault("options"))) Set(k, v);
            foreach (var (k, v) in ParseOptionLines(s.GetValueOrDefault("secret_options"))) Set(k, v);
            return env;
        }

        var backend = RcloneCatalog.Get(d.RcloneType) ?? throw new InvalidOperationException($"Unknown rclone backend '{d.RcloneType}'");
        Set("type", backend.Type);
        foreach (var f in backend.Fields)
        {
            var value = f.Secret ? s.GetValueOrDefault(f.Key) : p.GetValueOrDefault(f.Key);
            if (string.IsNullOrEmpty(value)) continue;
            Set(f.Key, f.Obscure ? await ObscureAsync(value, ct) : value);
        }
        return env;
    }

    internal static IEnumerable<(string Key, string Value)> ParseOptionLines(string? text)
    {
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            yield return (line[..i].Trim(), line[(i + 1)..].Trim());
        }
    }

    private static async Task<string> ObscureAsync(string value, CancellationToken ct)
    {
        var (code, stdout, err) = await RunProcessAsync("rclone", ["obscure", "-"], [], value, ct);
        if (code != 0) throw new InvalidOperationException($"rclone obscure failed: {LastLine(err)}");
        return stdout.Trim();
    }

    private static Task<(int, string, string)> RunRcloneAsync(string[] args, Dictionary<string, string> env, CancellationToken ct) =>
        RunProcessAsync("rclone", [.. args, "--config", "/dev/null"], env, null, ct);

    private static async Task<(int Code, string Stdout, string Stderr)> RunProcessAsync(
        string exe, string[] args, Dictionary<string, string> env, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"Could not start {exe}: {e.Message}"); }
        using (p)
        {
            if (stdin != null)
            {
                await p.StandardInput.WriteAsync(stdin);
                p.StandardInput.Close();
            }
            var o = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var e = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            return (p.ExitCode, await o, await e);
        }
    }

    private static string LastLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : lines[^1];
    }

    private record LsEntry(string Name, long Size, DateTimeOffset ModTime);
}

/// <summary>A read-only, seekable window over another stream (one Telegram upload part).</summary>
internal sealed class SubStream(Stream inner, long offset, long length) : Stream
{
    private long _pos;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, length); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int off, int count)
    {
        if (_pos >= length) return 0;
        inner.Position = offset + _pos;
        var n = inner.Read(buffer, off, (int)Math.Min(count, length - _pos));
        _pos += n;
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos >= length) return 0;
        inner.Position = offset + _pos;
        var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _pos)], ct);
        _pos += n;
        return n;
    }
    public override long Seek(long o, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => o,
        SeekOrigin.Current => _pos + o,
        _ => length + o,
    };
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int off, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { /* the caller owns the inner stream */ }
}

/// <summary>A Telegram upload that reached some chats but not all; <see cref="RemoteRef"/> records the ones that got it.</summary>
public sealed class PartialUploadException(string remoteRef, string message) : Exception(message)
{
    public string RemoteRef { get; } = remoteRef;
}
