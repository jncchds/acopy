using System.Text;
using ACopy.Api.Data;

namespace ACopy.Api.Engines;

/// <summary>What to run for one backup. Stdout of <see cref="Argv"/> is the backup, unless <see cref="OutputFile"/> is set.</summary>
public class DumpCommand
{
    public required IReadOnlyList<string> Argv { get; init; }
    public Dictionary<string, string> Env { get; init; } = [];
    /// <summary>Network mode only: the tool writes here instead of stdout (redis-cli --rdb).</summary>
    public string? OutputFile { get; init; }
    /// <summary>Network mode only: files to write (0600) before running and delete afterwards.</summary>
    public Dictionary<string, string> TempFiles { get; init; } = [];
    /// <summary>Network-mode SQLite: copy with the online backup API instead of running a process.</summary>
    public string? SqliteSourcePath { get; init; }
}

public record Credentials(string? User, string? Password, string? Database);

public static class DumpCommandBuilder
{
    public static DumpCommand Build(BackupTarget t, Credentials c, string tempDir)
    {
        var extra = SplitArgs(t.ExtraArgs);
        return t.Mode == ExecutionMode.Network ? BuildNetwork(t, c, extra, tempDir) : BuildExec(t, c, extra);
    }

    /// <summary>
    /// MariaDB/MySQL error 1577: the server was started with <c>event_scheduler=DISABLED</c> and refuses
    /// <c>SHOW EVENTS</c>, so a dump with <c>--events</c> fails.
    /// </summary>
    public static bool IsEventSchedulerDisabled(string error) =>
        error.Contains("event scheduler is disabled", StringComparison.OrdinalIgnoreCase) || error.Contains("(1577)", StringComparison.Ordinal);

    /// <summary>The same MariaDB/MySQL dump without <c>--events</c>; null when it has no such option.</summary>
    public static DumpCommand? WithoutEvents(DumpCommand cmd) => cmd.Argv.Contains("--events")
        ? new DumpCommand
        {
            Argv = cmd.Argv.Where(a => a != "--events").ToList(),
            Env = cmd.Env,
            OutputFile = cmd.OutputFile,
            TempFiles = cmd.TempFiles,
            SqliteSourcePath = cmd.SqliteSourcePath,
        }
        : null;

    private static DumpCommand BuildNetwork(BackupTarget t, Credentials c, List<string> extra, string tempDir)
    {
        var host = t.Host ?? "";
        if (host.Length == 0 && t.Engine is not (Engine.Sqlite or Engine.Custom))
            throw new InvalidOperationException("Host is required in network mode");
        var port = (t.Port ?? EngineCatalog.Get(t.Engine).DefaultPort ?? 0).ToString();
        var db = c.Database;
        switch (t.Engine)
        {
            case Engine.PostgreSql:
            {
                var argv = new List<string>();
                if (string.IsNullOrEmpty(db)) argv.AddRange(["pg_dumpall", "-h", host, "-p", port, "-w", "--clean", "--if-exists"]);
                else argv.AddRange(["pg_dump", "-h", host, "-p", port, "-w", "--clean", "--if-exists", "-d", db]);
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["-U", c.User]);
                argv.AddRange(extra);
                return new() { Argv = argv, Env = PasswordEnv("PGPASSWORD", c.Password) };
            }
            case Engine.MySql:
            case Engine.MariaDb:
            {
                // The bundled client is MariaDB's; it defaults to verifying TLS certificates, which
                // self-signed MySQL/MariaDB servers (the common docker case) fail. Encryption is still
                // used when the server offers it — only certificate verification is relaxed.
                var argv = new List<string>
                {
                    "mariadb-dump", "-h", host, "-P", port, "--protocol=TCP", "--skip-ssl-verify-server-cert",
                    "--single-transaction", "--routines", "--triggers", "--events",
                };
                if (!string.IsNullOrEmpty(c.User)) argv.Add($"--user={c.User}");
                argv.AddRange(extra);
                if (string.IsNullOrEmpty(db)) argv.Add("--all-databases");
                else argv.Add(db);
                return new() { Argv = argv, Env = PasswordEnv("MYSQL_PWD", c.Password) };
            }
            case Engine.MongoDb:
            {
                var argv = new List<string> { "mongodump", "--host", host, "--port", port, "--archive", "--quiet" };
                var temp = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(c.User))
                {
                    argv.AddRange(["--username", c.User]);
                    if (!extra.Any(a => a.StartsWith("--authenticationDatabase"))) argv.AddRange(["--authenticationDatabase", "admin"]);
                }
                if (!string.IsNullOrEmpty(c.Password))
                {
                    // Keep the password out of the process list: mongodump reads it from a YAML config file.
                    var cfg = Path.Combine(tempDir, $"mongo-{Guid.NewGuid():N}.yaml");
                    temp[cfg] = $"password: \"{c.Password.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\n";
                    argv.AddRange(["--config", cfg]);
                }
                if (!string.IsNullOrEmpty(db)) argv.AddRange(["--db", db]);
                argv.AddRange(extra);
                return new() { Argv = argv, TempFiles = temp };
            }
            case Engine.Redis:
            {
                var output = Path.Combine(tempDir, $"redis-{Guid.NewGuid():N}.rdb");
                var argv = new List<string> { "redis-cli", "-h", host, "-p", port };
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["--user", c.User]);
                argv.AddRange(extra);
                argv.AddRange(["--rdb", output]);
                return new() { Argv = argv, Env = PasswordEnv("REDISCLI_AUTH", c.Password), OutputFile = output };
            }
            case Engine.Sqlite:
                return new()
                {
                    Argv = [],
                    SqliteSourcePath = string.IsNullOrWhiteSpace(t.DatabaseName)
                        ? throw new InvalidOperationException("SQLite needs the file path in the Database field")
                        : t.DatabaseName,
                };
            case Engine.Custom:
                return new() { Argv = ["sh", "-c", RequireCommand(t)], Env = CustomEnv(t, c, host, port) };
            default:
                throw new InvalidOperationException($"{EngineCatalog.Get(t.Engine).Label} does not support network mode — use Docker exec");
        }
    }

    private static DumpCommand BuildExec(BackupTarget t, Credentials c, List<string> extra)
    {
        var db = c.Database;
        switch (t.Engine)
        {
            case Engine.PostgreSql:
            {
                var argv = new List<string>();
                if (string.IsNullOrEmpty(db)) argv.AddRange(["pg_dumpall", "-w", "--clean", "--if-exists"]);
                else argv.AddRange(["pg_dump", "-w", "--clean", "--if-exists", "-d", db]);
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["-U", c.User]);
                argv.AddRange(extra);
                return new() { Argv = argv, Env = PasswordEnv("PGPASSWORD", c.Password) };
            }
            case Engine.MySql:
            case Engine.MariaDb:
            {
                const string script = """
                    if command -v mariadb-dump >/dev/null 2>&1; then exec mariadb-dump "$@"; fi
                    if command -v mysqldump >/dev/null 2>&1; then exec mysqldump "$@"; fi
                    echo "neither mariadb-dump nor mysqldump found in the container" >&2; exit 127
                    """;
                var argv = new List<string> { "sh", "-c", script, "sh", "--single-transaction", "--routines", "--triggers", "--events" };
                if (!string.IsNullOrEmpty(c.User)) argv.Add($"--user={c.User}");
                argv.AddRange(extra);
                if (string.IsNullOrEmpty(db)) argv.Add("--all-databases");
                else argv.Add(db);
                return new() { Argv = argv, Env = PasswordEnv("MYSQL_PWD", c.Password) };
            }
            case Engine.MongoDb:
            {
                // The password travels as an env var and is expanded by the shell inside the container.
                var args = new List<string> { "--archive", "--quiet" };
                if (!string.IsNullOrEmpty(c.User))
                {
                    args.AddRange(["--username", c.User]);
                    if (!extra.Any(a => a.StartsWith("--authenticationDatabase"))) args.AddRange(["--authenticationDatabase", "admin"]);
                }
                if (!string.IsNullOrEmpty(db)) args.AddRange(["--db", db]);
                args.AddRange(extra);
                const string script = """
                    if [ -n "$ACOPY_PASSWORD" ]; then exec mongodump "$@" --password "$ACOPY_PASSWORD"; fi
                    exec mongodump "$@"
                    """;
                return new() { Argv = ["sh", "-c", script, "sh", .. args], Env = PasswordEnv("ACOPY_PASSWORD", c.Password) };
            }
            case Engine.Redis:
            {
                const string script = """
                    CLI=$(command -v redis-cli || command -v valkey-cli || command -v keydb-cli)
                    [ -n "$CLI" ] || { echo "redis-cli not found in the container" >&2; exit 127; }
                    F=/tmp/acopy-$$.rdb
                    "$CLI" "$@" --rdb "$F" >&2 && cat "$F"; rc=$?; rm -f "$F"; exit $rc
                    """;
                var args = new List<string>();
                if (!string.IsNullOrEmpty(c.User)) args.AddRange(["--user", c.User]);
                args.AddRange(extra);
                return new() { Argv = ["sh", "-c", script, "sh", .. args], Env = PasswordEnv("REDISCLI_AUTH", c.Password) };
            }
            case Engine.Sqlite:
            {
                if (string.IsNullOrWhiteSpace(t.DatabaseName)) throw new InvalidOperationException("SQLite needs the file path in the Database field");
                const string script = """
                    [ -f "$1" ] || { echo "no such file: $1" >&2; exit 2; }
                    if command -v sqlite3 >/dev/null 2>&1; then
                      F=/tmp/acopy-$$.sqlite
                      sqlite3 "$1" ".backup '$F'" >&2 && cat "$F"; rc=$?; rm -f "$F"; exit $rc
                    fi
                    echo "sqlite3 not found in the container; copying the file as-is (not guaranteed consistent while it is being written)" >&2
                    exec cat "$1"
                    """;
                return new() { Argv = ["sh", "-c", script, "sh", t.DatabaseName] };
            }
            case Engine.SqlServer:
            {
                if (string.IsNullOrWhiteSpace(db)) throw new InvalidOperationException("SQL Server needs a database name");
                if (db.IndexOfAny([']', '\'', ';']) >= 0) throw new InvalidOperationException("Invalid SQL Server database name");
                const string script = """
                    S=$(command -v sqlcmd || ls /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd 2>/dev/null | head -n1)
                    [ -n "$S" ] || { echo "sqlcmd not found in the container" >&2; exit 127; }
                    case "$S" in *18*) TRUST=-C ;; *) TRUST= ;; esac
                    F=/tmp/acopy-$$.bak
                    "$S" -S localhost -U "$1" $TRUST -b -Q "BACKUP DATABASE [$2] TO DISK = N'$F' WITH COPY_ONLY, INIT" >&2 && cat "$F"; rc=$?; rm -f "$F"; exit $rc
                    """;
                return new() { Argv = ["sh", "-c", script, "sh", c.User ?? "sa", db], Env = PasswordEnv("SQLCMDPASSWORD", c.Password) };
            }
            case Engine.Custom:
                return new() { Argv = ["sh", "-c", RequireCommand(t)], Env = CustomEnv(t, c, t.Host ?? "", t.Port?.ToString() ?? "") };
            default:
                throw new InvalidOperationException($"Unsupported engine {t.Engine}");
        }
    }

    private static string RequireCommand(BackupTarget t) =>
        string.IsNullOrWhiteSpace(t.ExtraArgs) ? throw new InvalidOperationException("Custom engine needs a command") : t.ExtraArgs;

    private static Dictionary<string, string> CustomEnv(BackupTarget t, Credentials c, string host, string port) => new()
    {
        ["ACOPY_HOST"] = host,
        ["ACOPY_PORT"] = port,
        ["ACOPY_USER"] = c.User ?? "",
        ["ACOPY_PASSWORD"] = c.Password ?? "",
        ["ACOPY_DATABASE"] = c.Database ?? "",
        ["ACOPY_NAME"] = t.Name,
    };

    private static Dictionary<string, string> PasswordEnv(string key, string? password) =>
        string.IsNullOrEmpty(password) ? [] : new() { [key] = password };

    /// <summary>Shell-like split: whitespace separates, single/double quotes group, backslash escapes in double quotes.</summary>
    public static List<string> SplitArgs(string? s)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return result;
        var cur = new StringBuilder();
        var inToken = false;
        char quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else if (ch == '\\' && quote == '"' && i + 1 < s.Length) cur.Append(s[++i]);
                else cur.Append(ch);
            }
            else if (ch is '"' or '\'') { quote = ch; inToken = true; }
            else if (char.IsWhiteSpace(ch))
            {
                if (inToken) { result.Add(cur.ToString()); cur.Clear(); inToken = false; }
            }
            else if (ch == '\\' && i + 1 < s.Length) { cur.Append(s[++i]); inToken = true; }
            else { cur.Append(ch); inToken = true; }
        }
        if (inToken) result.Add(cur.ToString());
        return result;
    }
}
