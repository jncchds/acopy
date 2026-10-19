using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ACopy.Api.Docker;

public record ContainerSummary(
    string Id, string Name, string Image, string State, string Status, IReadOnlyDictionary<string, string> Labels);

public record ContainerDetails(
    string Id, string Name, string Image, bool Running,
    IReadOnlyDictionary<string, string> Env, IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// Minimal Docker Engine API client over the unix socket — just what ACopy needs
/// (list, inspect, exec with demultiplexed stdout/stderr). Unversioned paths, so it
/// talks to whatever API version the daemon speaks.
/// </summary>
public class DockerClient
{
    private readonly HttpClient _http;
    private readonly DockerOptions _opt;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Human-readable endpoint, for the UI.</summary>
    public string Endpoint { get; }
    private readonly string? _unixPath;

    public DockerClient(IOptions<DockerOptions> opt)
    {
        _opt = opt.Value;
        var host = !string.IsNullOrWhiteSpace(_opt.Host) ? _opt.Host.Trim()
            : Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } envHost ? envHost
            : "unix://" + _opt.Socket;
        Endpoint = host;

        if (host.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            _unixPath = host["unix://".Length..];
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_unixPath), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            _http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = Timeout.InfiniteTimeSpan };
        }
        else if (host.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var tls = _opt.Tls || Environment.GetEnvironmentVariable("DOCKER_TLS_VERIFY") == "1" || host.StartsWith("https", StringComparison.OrdinalIgnoreCase);
            var authority = host[(host.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
            var handler = new SocketsHttpHandler();
            if (tls)
            {
                var certDir = _opt.CertPath ?? Environment.GetEnvironmentVariable("DOCKER_CERT_PATH");
                if (!string.IsNullOrEmpty(certDir))
                {
                    var client = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(
                        Path.Combine(certDir, "cert.pem"), Path.Combine(certDir, "key.pem"));
                    var ca = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(Path.Combine(certDir, "ca.pem"));
                    handler.SslOptions.ClientCertificates = [client];
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert == null) return false;
                        using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                        chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(ca);
                        chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                        return chain.Build(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert));
                    };
                }
            }
            _http = new HttpClient(handler) { BaseAddress = new Uri($"{(tls ? "https" : "http")}://{authority}/"), Timeout = Timeout.InfiniteTimeSpan };
        }
        else throw new InvalidOperationException($"Unsupported Docker host '{host}' — use unix:///path or tcp://host:port");
    }

    public bool Configured => _opt.Enabled && (_unixPath == null || File.Exists(_unixPath));

    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        if (!Configured) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var v = await _http.GetFromJsonAsync<JsonElement>("version", Json, cts.Token);
        return v.TryGetProperty("Version", out var ver) ? ver.GetString() : "unknown";
    }

    public async Task<List<ContainerSummary>> ListContainersAsync(CancellationToken ct)
    {
        var list = await _http.GetFromJsonAsync<List<ListItem>>("containers/json?all=true", Json, ct) ?? [];
        return list.Select(c => new ContainerSummary(
            c.Id,
            (c.Names?.FirstOrDefault() ?? c.Id[..12]).TrimStart('/'),
            c.Image ?? "",
            c.State ?? "",
            c.Status ?? "",
            c.Labels ?? new Dictionary<string, string>())).ToList();
    }

    public async Task<ContainerDetails?> InspectAsync(string nameOrId, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"containers/{Uri.EscapeDataString(nameOrId)}/json", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var c = await res.Content.ReadFromJsonAsync<InspectResult>(Json, ct) ?? throw new InvalidOperationException("Empty inspect response");
        var env = new Dictionary<string, string>();
        foreach (var kv in c.Config?.Env ?? [])
        {
            var i = kv.IndexOf('=');
            if (i > 0) env[kv[..i]] = kv[(i + 1)..];
        }
        return new ContainerDetails(
            c.Id, (c.Name ?? "").TrimStart('/'), c.Config?.Image ?? "",
            c.State?.Running == true, env, c.Config?.Labels ?? new Dictionary<string, string>());
    }

    /// <summary>
    /// Runs <paramref name="cmd"/> inside the container, streaming stdout into <paramref name="stdout"/>
    /// and stderr into <paramref name="stderr"/>. Returns the process exit code.
    /// </summary>
    public async Task<long> ExecAsync(
        string containerId, IReadOnlyList<string> cmd, IReadOnlyDictionary<string, string> env,
        Stream stdout, StringBuilder stderr, CancellationToken ct)
    {
        var create = new
        {
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            Cmd = cmd,
            Env = env.Select(kv => $"{kv.Key}={kv.Value}").ToArray(),
        };
        using var createRes = await _http.PostAsJsonAsync($"containers/{Uri.EscapeDataString(containerId)}/exec", create, Json, ct);
        if (!createRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"docker exec create failed: {(int)createRes.StatusCode} {await createRes.Content.ReadAsStringAsync(ct)}");
        var execId = (await createRes.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("Id").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"exec/{execId}/start")
        {
            Content = JsonContent.Create(new { Detach = false, Tty = false }),
        };
        using var startRes = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!startRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"docker exec start failed: {(int)startRes.StatusCode} {await startRes.Content.ReadAsStringAsync(ct)}");

        await using (var body = await startRes.Content.ReadAsStreamAsync(ct))
            await Demultiplex(body, stdout, stderr, ct);

        var inspect = await _http.GetFromJsonAsync<JsonElement>($"exec/{execId}/json", Json, ct);
        return inspect.TryGetProperty("ExitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt64() : -1;
    }

    /// <summary>
    /// Copies a local file into the container as <paramref name="containerDir"/>/<paramref name="name"/> (mode 0644,
    /// so non-root database processes can read it), via the archive endpoint — no tools needed in the container.
    /// </summary>
    public async Task PutFileAsync(string containerId, string localFile, string containerDir, string name, CancellationToken ct)
    {
        var tarPath = localFile + ".tar";
        try
        {
            await using (var tarFile = File.Create(tarPath))
            await using (var writer = new System.Formats.Tar.TarWriter(tarFile, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: false))
            {
                await using var data = File.OpenRead(localFile);
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name)
                {
                    DataStream = data,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = DateTimeOffset.UtcNow,
                };
                await writer.WriteEntryAsync(entry, ct);
            }
            await using var body = File.OpenRead(tarPath);
            using var content = new StreamContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
            using var res = await _http.PutAsync($"containers/{Uri.EscapeDataString(containerId)}/archive?path={Uri.EscapeDataString(containerDir)}", content, ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Copying the file into the container failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
        }
        finally
        {
            try { File.Delete(tarPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Docker's multiplexed stream: 8-byte header [type,0,0,0,len BE32] then payload.</summary>
    internal static async Task Demultiplex(Stream body, Stream stdout, StringBuilder stderr, CancellationToken ct)
    {
        var header = new byte[8];
        var buffer = new byte[81920];
        while (true)
        {
            if (!await ReadExactly(body, header, 8, ct)) return;
            var type = header[0];
            var remaining = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            while (remaining > 0)
            {
                var n = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
                if (n == 0) return;
                remaining -= n;
                if (type == 1) await stdout.WriteAsync(buffer.AsMemory(0, n), ct);
                else if (stderr.Length < 64_000) stderr.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }
        }
    }

    private static async Task<bool> ReadExactly(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private class ListItem
    {
        public string Id { get; set; } = "";
        public string[]? Names { get; set; }
        public string? Image { get; set; }
        public string? State { get; set; }
        public string? Status { get; set; }
        public Dictionary<string, string>? Labels { get; set; }
    }

    private class InspectResult
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public InspectConfig? Config { get; set; }
        public InspectState? State { get; set; }
    }

    private class InspectConfig
    {
        public string? Image { get; set; }
        public string[]? Env { get; set; }
        public Dictionary<string, string>? Labels { get; set; }
    }

    private class InspectState
    {
        [JsonPropertyName("Running")] public bool Running { get; set; }
    }
}
