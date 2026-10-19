using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ACopy.Api.Data;

namespace ACopy.Api.Services;

/// <summary>
/// Backup encryption. Two kinds, both decryptable without ACopy:
/// <list type="bullet">
/// <item><b>Passphrase</b> — the <c>openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -md sha256</c> format
/// ("Salted__" + 8-byte salt, key and IV from PBKDF2-HMAC-SHA256). Decrypt with
/// <c>openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in FILE -out OUT</c>.</item>
/// <item><b>age</b> — public-key encryption through the bundled <c>age</c> binary. Decrypt with
/// <c>age -d -i key.txt FILE &gt; OUT</c>.</item>
/// </list>
/// </summary>
public static class Encryption
{
    public const int Pbkdf2Iterations = 600_000;
    private static readonly byte[] Magic = "Salted__"u8.ToArray();

    public static string Extension(KeyKind kind) => kind == KeyKind.Age ? "age" : "enc";

    public static string DecryptHint(KeyKind kind) => kind == KeyKind.Age
        ? "age -d -i key.txt FILE > OUT"
        : $"openssl enc -d -aes-256-cbc -pbkdf2 -iter {Pbkdf2Iterations} -md sha256 -in FILE -out OUT";

    // ── Passphrase (OpenSSL-compatible) ──

    public static Stream PassphraseEncrypt(Stream output, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        output.Write(Magic);
        output.Write(salt);
        var (key, iv) = Derive(passphrase, salt);
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return new CryptoStream(output, aes.CreateEncryptor(key, iv), CryptoStreamMode.Write, leaveOpen: true);
    }

    public static Stream PassphraseDecrypt(Stream input, string passphrase)
    {
        var header = new byte[16];
        input.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidOperationException("Not an OpenSSL-encrypted file (missing Salted__ header)");
        var (key, iv) = Derive(passphrase, header[8..16]);
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return new CryptoStream(input, aes.CreateDecryptor(key, iv), CryptoStreamMode.Read, leaveOpen: false);
    }

    private static (byte[] Key, byte[] Iv) Derive(string passphrase, byte[] salt)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 48);
        return (bytes[..32], bytes[32..]);
    }

    // ── age ──

    public static List<string> ParseRecipients(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .ToList();

    public static bool IsValidRecipient(string r) =>
        (r.StartsWith("age1", StringComparison.Ordinal) && r.Length > 50 && !r.Contains(' '))
        || r.StartsWith("ssh-ed25519 ", StringComparison.Ordinal) || r.StartsWith("ssh-rsa ", StringComparison.Ordinal);

    /// <summary>Runs <c>age-keygen</c>; returns (identity file contents, public key).</summary>
    public static async Task<(string Identity, string Recipient)> GenerateAgeKeyAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("age-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("age-keygen is not available");
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("age-keygen failed: " + await p.StandardError.ReadToEndAsync(ct));
        var recipient = output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# public key: "))?["# public key: ".Length..]
            ?? throw new InvalidOperationException("age-keygen produced no public key");
        return (output, recipient);
    }

    /// <summary>Public key of an identity (<c>age-keygen -y</c>), to check a pasted private key.</summary>
    public static async Task<string?> RecipientOfIdentityAsync(string identity, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("age-keygen") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-y");
        using var p = Process.Start(psi);
        if (p == null) return null;
        await p.StandardInput.WriteAsync(identity);
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? output.Trim().Split('\n')[0].Trim() : null;
    }

    /// <summary>
    /// Starts <c>age</c> writing ciphertext to <paramref name="outputPath"/>. Write plaintext into
    /// <see cref="AgeProcess.Input"/>, then await <see cref="AgeProcess.CompleteAsync"/>.
    /// </summary>
    public static AgeProcess StartAgeEncrypt(IEnumerable<string> recipients, string outputPath)
    {
        var psi = new ProcessStartInfo("age") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var r in recipients) { psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(r); }
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);
        return new AgeProcess(psi);
    }

    /// <summary>Decrypts <paramref name="inputPath"/> to <paramref name="outputPath"/> with the identity (written to a 0600 temp file).</summary>
    public static async Task AgeDecryptAsync(string identity, string inputPath, string outputPath, string workDir, CancellationToken ct)
    {
        var idFile = Path.Combine(workDir, $"age-id-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(idFile, identity, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(idFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var psi = new ProcessStartInfo("age") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-d", "-i", idFile, "-o", outputPath, inputPath }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("age is not available");
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("age decryption failed: " + err.Trim());
        }
        finally
        {
            try { File.Delete(idFile); } catch { /* best effort */ }
        }
    }
}

public sealed class AgeProcess : IAsyncDisposable
{
    private readonly Process _p;
    private readonly Task<string> _stderr;

    public AgeProcess(ProcessStartInfo psi)
    {
        try { _p = Process.Start(psi) ?? throw new InvalidOperationException("age is not available"); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"Could not start age: {e.Message}"); }
        _stderr = _p.StandardError.ReadToEndAsync();
    }

    public Stream Input => _p.StandardInput.BaseStream;

    public async Task CompleteAsync(CancellationToken ct)
    {
        _p.StandardInput.Close();
        await _p.WaitForExitAsync(ct);
        if (_p.ExitCode != 0) throw new InvalidOperationException("age encryption failed: " + (await _stderr).Trim());
    }

    public ValueTask DisposeAsync()
    {
        try { if (!_p.HasExited) _p.Kill(); } catch { /* gone */ }
        _p.Dispose();
        return ValueTask.CompletedTask;
    }
}
