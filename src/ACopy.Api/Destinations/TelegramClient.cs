using System.Net.Http.Headers;
using System.Text.Json;

namespace ACopy.Api.Destinations;

public record TelegramTarget(string Token, string ChatId, int? ThreadId, string ApiUrl);

/// <summary>Minimal Telegram Bot API client: messages, documents, deletion.</summary>
public class TelegramClient(IHttpClientFactory httpFactory)
{
    public const string DefaultApi = "https://api.telegram.org";

    private HttpClient Client(TimeSpan timeout)
    {
        var c = httpFactory.CreateClient("telegram");
        c.Timeout = timeout;
        return c;
    }

    private static string Url(TelegramTarget t, string method) => $"{t.ApiUrl.TrimEnd('/')}/bot{t.Token}/{method}";

    public async Task SendMessageAsync(TelegramTarget t, string html, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["chat_id"] = t.ChatId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = "true",
        };
        if (t.ThreadId != null) form["message_thread_id"] = t.ThreadId.Value.ToString();
        using var res = await Client(TimeSpan.FromSeconds(20)).PostAsync(Url(t, "sendMessage"), new FormUrlEncodedContent(form), ct);
        await EnsureOk(res, ct);
    }

    /// <summary>Uploads a document; returns the message id.</summary>
    public async Task<long> SendDocumentAsync(TelegramTarget t, Stream content, string fileName, string? captionHtml, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(t.ChatId), "chat_id" },
        };
        if (t.ThreadId != null) form.Add(new StringContent(t.ThreadId.Value.ToString()), "message_thread_id");
        if (captionHtml != null)
        {
            form.Add(new StringContent(captionHtml), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
        }
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "document", fileName);

        using var res = await Client(TimeSpan.FromMinutes(30)).PostAsync(Url(t, "sendDocument"), form, ct);
        var json = await EnsureOk(res, ct);
        return json.GetProperty("result").GetProperty("message_id").GetInt64();
    }

    public async Task DeleteMessageAsync(TelegramTarget t, long messageId, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["chat_id"] = t.ChatId, ["message_id"] = messageId.ToString() };
        using var res = await Client(TimeSpan.FromSeconds(20)).PostAsync(Url(t, "deleteMessage"), new FormUrlEncodedContent(form), ct);
        await EnsureOk(res, ct);
    }

    /// <summary>getMe — validates the token; returns the bot's @username.</summary>
    public async Task<string> GetMeAsync(TelegramTarget t, CancellationToken ct)
    {
        using var res = await Client(TimeSpan.FromSeconds(15)).GetAsync(Url(t, "getMe"), ct);
        var json = await EnsureOk(res, ct);
        return json.GetProperty("result").TryGetProperty("username", out var u) ? u.GetString() ?? "bot" : "bot";
    }

    private static async Task<JsonElement> EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        JsonElement json;
        try { json = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { throw new InvalidOperationException($"Telegram: HTTP {(int)res.StatusCode}"); }
        if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var desc = json.TryGetProperty("description", out var d) ? d.GetString() : $"HTTP {(int)res.StatusCode}";
            throw new InvalidOperationException($"Telegram: {desc}");
        }
        return json;
    }

    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
