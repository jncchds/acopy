using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Destinations;

/// <summary>One bot + one chat (+ optional forum topic) on one Bot API server.</summary>
public record TelegramTarget(string Token, string ChatId, int? ThreadId, string ApiUrl)
{
    public TelegramTarget ForChat(string chatId) => this with { ChatId = chatId };
}

public record SentDocument(long MessageId, string? FileId);

/// <summary>Minimal Telegram Bot API client: messages, documents, deletion.</summary>
public class TelegramClient(IHttpClientFactory httpFactory, IOptions<TelegramOptions> options)
{
    public const string PublicApi = "https://api.telegram.org";

    /// <summary>The server used when a destination or the settings leave the URL blank (<c>ACOPY_Telegram__ApiUrl</c>).</summary>
    public string DefaultApiUrl => string.IsNullOrWhiteSpace(options.Value.ApiUrl) ? PublicApi : options.Value.ApiUrl.Trim().TrimEnd('/');

    public string ResolveApiUrl(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultApiUrl : configured.Trim().TrimEnd('/');

    /// <summary>The public server limits bot uploads to 50 MB; a self-hosted one (<c>--local</c>) to 2000 MB.</summary>
    public static bool IsPublic(string apiUrl) => apiUrl.TrimEnd('/').Equals(PublicApi, StringComparison.OrdinalIgnoreCase);

    /// <summary>A comma-separated chat list (<c>-1001234, 5678, @channel</c>) → distinct chat ids.</summary>
    public static List<string> ParseChats(string? list) =>
        (list ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Chat ids are numbers (<c>-100…</c> for groups and channels) or a public <c>@username</c>.</summary>
    public static bool IsValidChat(string chat) =>
        long.TryParse(chat, out _) || (chat.StartsWith('@') && chat.Length > 1 && chat[1..].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

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

    /// <summary>Uploads a document; returns the message id and the file id (to send the same file to more chats).</summary>
    public async Task<SentDocument> SendDocumentAsync(TelegramTarget t, Stream content, string fileName, string? captionHtml, CancellationToken ct)
    {
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var form = DocumentForm(t, captionHtml);
        form.Add(file, "document", fileName);
        return await PostDocument(t, form, TimeSpan.FromMinutes(30), ct);
    }

    /// <summary>Sends an already uploaded file to another chat by its file id — nothing is uploaded again.</summary>
    public async Task<SentDocument> SendDocumentByIdAsync(TelegramTarget t, string fileId, string? captionHtml, CancellationToken ct)
    {
        using var form = DocumentForm(t, captionHtml);
        form.Add(new StringContent(fileId), "document");
        return await PostDocument(t, form, TimeSpan.FromMinutes(2), ct);
    }

    private static MultipartFormDataContent DocumentForm(TelegramTarget t, string? captionHtml)
    {
        var form = new MultipartFormDataContent { { new StringContent(t.ChatId), "chat_id" } };
        if (t.ThreadId != null) form.Add(new StringContent(t.ThreadId.Value.ToString()), "message_thread_id");
        if (captionHtml != null)
        {
            form.Add(new StringContent(captionHtml), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
        }
        return form;
    }

    private async Task<SentDocument> PostDocument(TelegramTarget t, MultipartFormDataContent form, TimeSpan timeout, CancellationToken ct)
    {
        using var res = await Client(timeout).PostAsync(Url(t, "sendDocument"), form, ct);
        var result = (await EnsureOk(res, ct)).GetProperty("result");
        var fileId = result.TryGetProperty("document", out var doc) && doc.TryGetProperty("file_id", out var f) ? f.GetString() : null;
        return new SentDocument(result.GetProperty("message_id").GetInt64(), fileId);
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

    /// <summary>
    /// logOut on the public server — the one-time step before a bot moves to a self-hosted Bot API server.
    /// Afterwards the bot cannot log back in to api.telegram.org for 10 minutes.
    /// </summary>
    public async Task LogOutFromPublicAsync(string token, CancellationToken ct)
    {
        using var res = await Client(TimeSpan.FromSeconds(20)).PostAsync($"{PublicApi}/bot{token}/logOut", null, ct);
        await EnsureOk(res, ct);
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

/// <summary>
/// The remote reference of a Telegram upload: which messages in which chats hold the file, so retention can delete
/// them. Format <c>chat:id,id;chat:id,id</c>. Older uploads stored only <c>id,id</c> (in the destination's chat).
/// </summary>
public static class TelegramRef
{
    public static string Format(IEnumerable<KeyValuePair<string, List<long>>> messages) =>
        string.Join(';', messages.Where(kv => kv.Value.Count > 0).Select(kv => $"{kv.Key}:{string.Join(',', kv.Value)}"));

    /// <param name="legacyChat">The chat of a reference written before multi-chat uploads (the destination's chat).</param>
    public static Dictionary<string, List<long>> Parse(string? remoteRef, string? legacyChat)
    {
        var result = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var group in (remoteRef ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = group.LastIndexOf(':');
            var chat = colon >= 0 ? group[..colon] : legacyChat;
            if (string.IsNullOrEmpty(chat)) continue;
            var ids = group[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.TryParse(s, out var id) ? id : (long?)null).OfType<long>().ToList();
            if (ids.Count == 0) continue;
            if (result.TryGetValue(chat, out var list)) list.AddRange(ids);
            else result[chat] = ids;
        }
        return result;
    }
}
