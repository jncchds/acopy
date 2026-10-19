using System.Security.Cryptography;
using ACopy.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ACopy.Api.Services;

public record AppSettingsDto(
    string TimeZone, string? WebhookUrl, bool NotifyOnSuccess,
    bool TelegramHasToken, string? TelegramChatId, string? TelegramThreadId, string? TelegramApiUrl);

/// <summary>Key/value settings in the database, edited from the Settings page.</summary>
public class SettingsStore(IServiceScopeFactory scopes)
{
    public const string KeyTimeZone = "timeZone";
    public const string KeyWebhookUrl = "webhookUrl";
    public const string KeyNotifyOnSuccess = "notifyOnSuccess";
    public const string KeyPasswordHash = "auth.passwordHash";
    public const string KeyTelegramToken = "telegram.botToken"; // Data Protection–encrypted
    public const string KeyTelegramChat = "telegram.chatId";
    public const string KeyTelegramThread = "telegram.threadId";
    public const string KeyTelegramApi = "telegram.apiUrl";

    private Dictionary<string, string?>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string?> GetAsync(string key)
    {
        var all = await LoadAsync();
        return all.TryGetValue(key, out var v) ? v : null;
    }

    public async Task SetAsync(string key, string? value)
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Settings.FindAsync(key);
            if (row == null) db.Settings.Add(new AppSetting { Key = key, Value = value });
            else row.Value = value;
            await db.SaveChangesAsync();
            _cache = null;
        }
        finally { _lock.Release(); }
    }

    public async Task<AppSettingsDto> GetPublicAsync() => new(
        await GetTimeZoneIdAsync(),
        await GetAsync(KeyWebhookUrl),
        await GetAsync(KeyNotifyOnSuccess) == "true",
        !string.IsNullOrEmpty(await GetAsync(KeyTelegramToken)),
        await GetAsync(KeyTelegramChat),
        await GetAsync(KeyTelegramThread),
        await GetAsync(KeyTelegramApi));

    public async Task<string> GetTimeZoneIdAsync()
    {
        var tz = await GetAsync(KeyTimeZone);
        if (!string.IsNullOrWhiteSpace(tz)) return tz;
        var env = Environment.GetEnvironmentVariable("TZ");
        return string.IsNullOrWhiteSpace(env) ? "UTC" : env;
    }

    public async Task<TimeZoneInfo> GetTimeZoneAsync()
    {
        var id = await GetTimeZoneIdAsync();
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : TimeZoneInfo.Utc;
    }

    private async Task<Dictionary<string, string?>> LoadAsync()
    {
        var c = _cache;
        if (c != null) return c;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        c = await db.Settings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value);
        _cache = c;
        return c;
    }

    // ── Password hashing (PBKDF2-SHA256) ──
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
