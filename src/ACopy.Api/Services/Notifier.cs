using System.Net.Http.Json;
using ACopy.Api.Data;
using ACopy.Api.Destinations;

namespace ACopy.Api.Services;

/// <summary>
/// Sends run outcomes to the configured channels: a JSON webhook (payload carries both <c>text</c> — Slack,
/// Mattermost — and <c>content</c> — Discord) and/or a Telegram chat.
/// </summary>
public class Notifier(SettingsStore settings, Secrets secrets, TelegramClient telegram, IHttpClientFactory httpFactory, ILogger<Notifier> log)
{
    public async Task NotifyAsync(BackupTarget target, BackupRun run)
    {
        var failedUploads = run.Uploads.Where(u => u.Status == UploadStatus.Failed).ToList();
        var ok = run.Status == RunStatus.Succeeded && failedUploads.Count == 0;
        if (ok && await settings.GetAsync(SettingsStore.KeyNotifyOnSuccess) != "true") return;

        var what = run.Kind == RunKind.Restore ? "restore" : "backup";
        string text, html;
        if (run.Status == RunStatus.Succeeded && failedUploads.Count > 0)
        {
            var list = string.Join(", ", failedUploads.Select(u => $"{u.Destination?.Name ?? "#" + u.DestinationId}: {u.Error}"));
            text = $"⚠️ ACopy: {what} of {target.Name} succeeded, but uploads failed — {list}";
            html = $"⚠️ <b>{E(target.Name)}</b>: {what} succeeded, uploads failed\n{E(list)}";
        }
        else if (ok)
        {
            text = run.Kind == RunKind.Restore
                ? $"✅ ACopy: {target.Name} restored from {run.FilePath}"
                : $"✅ ACopy: backup of {target.Name} succeeded ({FormatSize(run.SizeBytes)}) → {run.FilePath}";
            html = run.Kind == RunKind.Restore
                ? $"✅ <b>{E(target.Name)}</b> restored from <code>{E(run.FilePath ?? "")}</code>"
                : $"✅ <b>{E(target.Name)}</b> backed up · {FormatSize(run.SizeBytes)}\n<code>{E(run.FilePath ?? "")}</code>";
        }
        else
        {
            var status = run.Status.ToString().ToLowerInvariant();
            text = $"❌ ACopy: {what} of {target.Name} {status}: {run.Error}";
            html = $"❌ <b>{E(target.Name)}</b>: {what} {status}\n{E(run.Error ?? "")}";
        }

        var payload = new
        {
            @event = ok ? $"{what}.succeeded" : run.Status == RunStatus.Succeeded ? "upload.failed" : $"{what}.failed",
            text,
            content = text,
            target = new { target.Id, target.Name, engine = target.Engine.ToString() },
            run = new
            {
                run.Id, kind = run.Kind.ToString(), status = run.Status.ToString(), run.StartedAt, run.FinishedAt,
                run.FilePath, run.SizeBytes, run.Sha256, run.Error,
                uploads = run.Uploads.Select(u => new { destination = u.Destination?.Name, status = u.Status.ToString(), u.Error }),
            },
        };

        var url = await settings.GetAsync(SettingsStore.KeyWebhookUrl);
        if (!string.IsNullOrWhiteSpace(url)) await PostAsync(url, payload);
        if (await TelegramTargetAsync() is { } tg)
        {
            foreach (var chat in tg.Chats)
            {
                try { await telegram.SendMessageAsync(tg.Bot.ForChat(chat), html, CancellationToken.None); }
                catch (Exception e) { log.LogWarning(e, "Telegram notification to chat {Chat} failed", chat); }
            }
        }
    }

    /// <summary>The notification bot and its chats (the chat id setting is a comma-separated list).</summary>
    public async Task<(TelegramTarget Bot, List<string> Chats)?> TelegramTargetAsync()
    {
        var token = secrets.Unprotect(await settings.GetAsync(SettingsStore.KeyTelegramToken));
        var chats = TelegramClient.ParseChats(await settings.GetAsync(SettingsStore.KeyTelegramChat));
        if (string.IsNullOrWhiteSpace(token) || chats.Count == 0) return null;
        int? thread = int.TryParse(await settings.GetAsync(SettingsStore.KeyTelegramThread), out var t) ? t : null;
        var api = telegram.ResolveApiUrl(await settings.GetAsync(SettingsStore.KeyTelegramApi));
        return (new TelegramTarget(token, chats[0], thread, api), chats);
    }

    public async Task<string?> SendTestAsync(string url)
    {
        try
        {
            using var res = await Client().PostAsJsonAsync(url, new { @event = "test", text = "ACopy test notification", content = "ACopy test notification" });
            return res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}";
        }
        catch (Exception e) { return e.Message; }
    }

    public async Task<string?> SendTelegramTestAsync()
    {
        var tg = await TelegramTargetAsync();
        if (tg == null) return "Bot token and chat id are required";
        var errors = new List<string>();
        foreach (var chat in tg.Value.Chats)
        {
            try { await telegram.SendMessageAsync(tg.Value.Bot.ForChat(chat), "✅ ACopy notifications work.", CancellationToken.None); }
            catch (Exception e) { errors.Add(tg.Value.Chats.Count == 1 ? e.Message : $"chat {chat}: {e.Message}"); }
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private async Task PostAsync(string url, object payload)
    {
        try
        {
            using var res = await Client().PostAsJsonAsync(url, payload);
            if (!res.IsSuccessStatusCode) log.LogWarning("Webhook returned {Status}", (int)res.StatusCode);
        }
        catch (Exception e) { log.LogWarning(e, "Webhook failed"); }
    }

    private HttpClient Client()
    {
        var c = httpFactory.CreateClient("webhook");
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private static string E(string s) => TelegramClient.Escape(s);

    public static string FormatSize(long? bytes)
    {
        if (bytes is null) return "?";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes.Value;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
