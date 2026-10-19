using System.Net.Http.Json;
using ACopy.Api.Data;

namespace ACopy.Api.Services;

/// <summary>
/// Posts a JSON event to the configured webhook. The payload carries both <c>text</c> (Slack, Mattermost,
/// ntfy-compatible relays) and <c>content</c> (Discord) so common chat webhooks work without an adapter.
/// </summary>
public class Notifier(SettingsStore settings, IHttpClientFactory httpFactory, ILogger<Notifier> log)
{
    public async Task NotifyAsync(BackupTarget target, BackupRun run)
    {
        var url = await settings.GetAsync(SettingsStore.KeyWebhookUrl);
        if (string.IsNullOrWhiteSpace(url)) return;
        var ok = run.Status == RunStatus.Succeeded;
        if (ok && await settings.GetAsync(SettingsStore.KeyNotifyOnSuccess) != "true") return;

        var text = ok
            ? $"✅ ACopy: backup of {target.Name} succeeded ({FormatSize(run.SizeBytes)}) → {run.FilePath}"
            : $"❌ ACopy: backup of {target.Name} {run.Status.ToString().ToLowerInvariant()}: {run.Error}";
        await PostAsync(url, new
        {
            @event = ok ? "backup.succeeded" : "backup.failed",
            text,
            content = text,
            target = new { target.Id, target.Name, engine = target.Engine.ToString() },
            run = new { run.Id, status = run.Status.ToString(), run.StartedAt, run.FinishedAt, run.FilePath, run.SizeBytes, run.Sha256, run.Error },
        });
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
