using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ACopy.Api.Data;
using ACopy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace ACopy.Api.Destinations;

public record DestinationTestResult(bool Ok, string Message);

/// <summary>Copies finished backups to destinations (rclone backends, Telegram) and applies remote retention.</summary>
public class DestinationService(Secrets secrets, TelegramClient telegram)
{
    private const string RemoteName = "ACOPY";

    // ── Upload ──

    /// <summary>Uploads one backup file; returns the remote reference (path, or Telegram message ids).</summary>
    public async Task<string> UploadAsync(Destination d, BackupTarget t, BackupRun run, string localPath, string folderRel, string fileName, CancellationToken ct)
    {
        if (d.Kind == DestinationKind.Telegram) return await UploadTelegramAsync(d, t, run, localPath, fileName, ct);

        var remote = Join(Join(RemoteBase(d), folderRel), fileName);
        var env = await RcloneEnvAsync(d, ct);
        var (code, _, err) = await RunRcloneAsync(["copyto", localPath, remote, "--retries", "3", "--low-level-retries", "10"], env, ct);
        if (code != 0) throw new InvalidOperationException($"rclone copyto failed: {LastLine(err)}");
        return remote;
    }

    private async Task<string> UploadTelegramAsync(Destination d, BackupTarget t, BackupRun run, string localPath, string fileName, CancellationToken ct)
    {
        var tg = TelegramOf(d);
        var p = Params(d);
        var defaultLimit = tg.ApiUrl.TrimEnd('/') == TelegramClient.DefaultApi ? 49 : 1990;
        var partMb = p.TryGetValue("maxPartMb", out var m) && int.TryParse(m, out var mb) && mb > 0 ? mb : defaultLimit;
        var partBytes = (long)partMb * 1024 * 1024;

        var size = new FileInfo(localPath).Length;
        var parts = (int)Math.Max(1, (size + partBytes - 1) / partBytes);
        var ids = new List<long>();
        await using var file = File.OpenRead(localPath);
        for (var i = 0; i < parts; i++)
        {
            var offset = i * partBytes;
            var len = Math.Min(partBytes, size - offset);
            var name = parts == 1 ? fileName : $"{fileName}.{i + 1:000}";
            var caption = new StringBuilder()
                .Append($"🗄 <b>{TelegramClient.Escape(t.Name)}</b>\n")
                .Append($"<code>{TelegramClient.Escape(fileName)}</code> · {Notifier.FormatSize(size)}");
            if (parts > 1) caption.Append($"\npart {i + 1}/{parts} — join with <code>cat {TelegramClient.Escape(fileName)}.* &gt; {TelegramClient.Escape(fileName)}</code>");
            if (i == 0 && run.Sha256 != null) caption.Append($"\nsha256 <code>{run.Sha256}</code>");
            await using var part = new SubStream(file, offset, len);
            ids.Add(await telegram.SendDocumentAsync(tg, part, name, caption.ToString(), ct));
        }
        return string.Join(',', ids);
    }

    // ── Remote retention ──

    public async Task PruneAsync(Destination d, BackupTarget t, string folderRel, string justWritten, AppDbContext db, StringBuilder runLog, CancellationToken ct)
    {
        var policy = new RetentionPolicy(d.Keep ?? t.Keep, d.Keep != null || d.MaxAgeDays != null ? d.MaxAgeDays : t.MaxAgeDays);
        if (d.Kind == DestinationKind.Telegram)
        {
            await PruneTelegramAsync(d, t, policy, db, runLog, ct);
            return;
        }

        var dir = Join(RemoteBase(d), folderRel);
        var env = await RcloneEnvAsync(d, ct);
        var (code, stdout, err) = await RunRcloneAsync(["lsjson", dir, "--files-only"], env, ct);
        if (code != 0) { runLog.AppendLine($"{d.Name}: listing for retention failed: {LastLine(err)}"); return; }
        var entries = JsonSerializer.Deserialize<List<LsEntry>>(stdout, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var files = entries.Select(e => new FileEntry(e.Name, e.ModTime.UtcDateTime));
        var prune = BackupFiles.FilesToPrune(BackupFiles.BaseName(t), t.Naming, policy, files, justWritten, DateTime.UtcNow);
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

    private async Task PruneTelegramAsync(Destination d, BackupTarget t, RetentionPolicy policy, AppDbContext db, StringBuilder runLog, CancellationToken ct)
    {
        var uploads = await db.Uploads.Include(u => u.Run)
            .Where(u => u.DestinationId == d.Id && u.Status == UploadStatus.Succeeded && !u.Deleted && u.Run!.TargetId == t.Id)
            .ToListAsync(ct);
        var ordered = uploads.OrderByDescending(u => u.Run!.StartedAt).ToList();
        var keep = t.Naming == NamingMode.Rolling ? Math.Max(1, policy.Keep) : policy.Keep;
        var tg = TelegramOf(d);
        for (var i = 1; i < ordered.Count; i++)
        {
            var u = ordered[i];
            var beyond = keep > 0 && i >= keep;
            var old = policy.MaxAgeDays is > 0 && DateTimeOffset.UtcNow - u.Run!.StartedAt > TimeSpan.FromDays(policy.MaxAgeDays.Value);
            if (!beyond && !old) continue;
            foreach (var id in (u.RemoteRef ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try { await telegram.DeleteMessageAsync(tg, long.Parse(id), ct); }
                catch (Exception e) { runLog.AppendLine($"{d.Name}: could not delete message {id}: {e.Message}"); }
            }
            u.Deleted = true;
            runLog.AppendLine($"{d.Name}: retention removed the upload of run #{u.RunId}");
        }
        await db.SaveChangesAsync(ct);
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
                var tg = TelegramOf(d);
                var bot = await telegram.GetMeAsync(tg, ct);
                await telegram.SendMessageAsync(tg, $"✅ ACopy destination <b>{TelegramClient.Escape(d.Name)}</b> works.", ct);
                return new(true, $"@{bot} posted a test message to chat {tg.ChatId}");
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

    public TelegramTarget TelegramOf(Destination d)
    {
        var p = Params(d);
        var s = secrets.UnprotectMap(d.SecretsProtected);
        if (!s.TryGetValue("botToken", out var token) || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Telegram bot token is missing");
        if (!p.TryGetValue("chatId", out var chat) || string.IsNullOrWhiteSpace(chat)) throw new InvalidOperationException("Telegram chat id is missing");
        int? thread = p.TryGetValue("threadId", out var th) && int.TryParse(th, out var ti) ? ti : null;
        var api = p.TryGetValue("apiUrl", out var a) && !string.IsNullOrWhiteSpace(a) ? a.Trim() : TelegramClient.DefaultApi;
        return new TelegramTarget(token.Trim(), chat.Trim(), thread, api);
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
