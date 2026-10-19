using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ACopy.Api.Services;

/// <summary>Data Protection for stored secrets (destination credentials, key material, bot tokens).</summary>
public class Secrets(IDataProtectionProvider dp)
{
    private readonly IDataProtector _p = dp.CreateProtector("ACopy.Secrets");

    public string Protect(string plain) => _p.Protect(plain);
    public string? Unprotect(string? cipher) => string.IsNullOrEmpty(cipher) ? null : _p.Unprotect(cipher);

    public Dictionary<string, string> UnprotectMap(string? cipher) =>
        Unprotect(cipher) is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];

    public string? ProtectMap(Dictionary<string, string> map) =>
        map.Count == 0 ? null : Protect(JsonSerializer.Serialize(map));
}
