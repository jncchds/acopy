using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using ACopy.Api.Data;
using ACopy.Api.Docker;
using ACopy.Api.Engines;
using Microsoft.Data.Sqlite;

namespace ACopy.Api.Services;

public record TestResult(bool Ok, string Message, string? Details);

/// <summary>A cheap "would a backup work?" check — connects and asks for a version, without dumping anything.</summary>
public class ConnectionTester(BackupRunner runner, DockerClient docker)
{
    public async Task<TestResult> TestAsync(BackupTarget t, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var log = new StringBuilder();
        try
        {
            var (c, containerId) = await runner.ResolveAsync(t, log, cts.Token);
            return t.Mode == ExecutionMode.DockerExec
                ? await TestExecAsync(t, c, containerId!, log, cts.Token)
                : await TestNetworkAsync(t, c, log, cts.Token);
        }
        catch (OperationCanceledException) { return new(false, "Timed out after 30 s", log.ToString()); }
        catch (Exception e) { return new(false, e.Message, log.Length > 0 ? log.ToString() : null); }
    }

    private static async Task<TestResult> TestNetworkAsync(BackupTarget t, Credentials c, StringBuilder log, CancellationToken ct)
    {
        var host = t.Host ?? "";
        var port = (t.Port ?? EngineCatalog.Get(t.Engine).DefaultPort ?? 0).ToString();
        switch (t.Engine)
        {
            case Engine.PostgreSql:
            {
                var argv = new List<string> { "psql", "-h", host, "-p", port, "-w", "-At", "-d", c.Database ?? "postgres", "-c", "select version()" };
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["-U", c.User]);
                return await RunAsync(argv, Env("PGPASSWORD", c.Password), log, ct);
            }
            case Engine.MySql:
            case Engine.MariaDb:
            {
                var argv = new List<string> { "mariadb", "-h", host, "-P", port, "--protocol=TCP", "--skip-ssl-verify-server-cert", "-N", "-e", "select version()" };
                if (!string.IsNullOrEmpty(c.User)) argv.Add($"--user={c.User}");
                return await RunAsync(argv, Env("MYSQL_PWD", c.Password), log, ct);
            }
            case Engine.Redis:
            {
                var argv = new List<string> { "redis-cli", "-h", host, "-p", port };
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["--user", c.User]);
                argv.Add("PING");
                var r = await RunAsync(argv, Env("REDISCLI_AUTH", c.Password), log, ct);
                return r.Ok && !r.Message.Contains("PONG") ? r with { Ok = false } : r;
            }
            case Engine.MongoDb:
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, int.Parse(port), ct);
                return new(true, $"TCP connection to {host}:{port} succeeded (credentials are checked on the first backup)", null);
            }
            case Engine.Sqlite:
            {
                var path = t.DatabaseName ?? "";
                if (!File.Exists(path)) return new(false, $"File not found inside ACopy: {path}", "Mount the file (or its folder) into the ACopy container.");
                await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "select count(*) from sqlite_master";
                var n = await cmd.ExecuteScalarAsync(ct);
                return new(true, $"Opened {path} — {n} schema objects", null);
            }
            case Engine.Custom:
                return new(true, "Custom commands are not tested — run a backup to check", null);
            default:
                return new(false, "Not supported in network mode", null);
        }
    }

    private async Task<TestResult> TestExecAsync(BackupTarget t, Credentials c, string containerId, StringBuilder log, CancellationToken ct)
    {
        var tool = t.Engine switch
        {
            Engine.PostgreSql => string.IsNullOrEmpty(c.Database) ? "pg_dumpall" : "pg_dump",
            Engine.MySql or Engine.MariaDb => "mariadb-dump mysqldump",
            Engine.MongoDb => "mongodump",
            Engine.Redis => "redis-cli valkey-cli keydb-cli",
            Engine.Sqlite => "sqlite3 cat",
            Engine.SqlServer => "sqlcmd /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd",
            _ => "sh",
        };
        const string script = """
            for t in "$@"; do
              if command -v "$t" >/dev/null 2>&1 || [ -x "$t" ]; then echo "found $t"; exit 0; fi
            done
            echo "none of: $*" >&2; exit 127
            """;
        using var stdout = new MemoryStream();
        var stderr = new StringBuilder();
        var code = await docker.ExecAsync(containerId, ["sh", "-c", script, "sh", .. tool.Split(' ')], new Dictionary<string, string>(), stdout, stderr, ct);
        var outText = Encoding.UTF8.GetString(stdout.ToArray()).Trim();
        var details = (log + stderr.ToString()).Trim();
        if (code != 0) return new(false, $"Dump tool not found in container '{t.ContainerName}' ({stderr.ToString().Trim()})", details);
        var note = t.Engine == Engine.Sqlite && outText == "found cat"
            ? " — sqlite3 is missing, so the file will be copied as-is"
            : "";
        return new(true, $"Container '{t.ContainerName}' is running; {outText}{note}", details.Length > 0 ? details : null);
    }

    private static Dictionary<string, string> Env(string key, string? v) => string.IsNullOrEmpty(v) ? [] : new() { [key] = v };

    private static async Task<TestResult> RunAsync(List<string> argv, Dictionary<string, string> env, StringBuilder log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var o = (await stdout).Trim();
        var e = (await stderr).Trim();
        var details = (log + e).Trim();
        return p.ExitCode == 0
            ? new(true, o.Length > 0 ? o.Split('\n')[0] : "OK", details.Length > 0 ? details : null)
            : new(false, e.Length > 0 ? e.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1] : $"exit code {p.ExitCode}", details);
    }
}
