using ACopy.Api.Data;

namespace ACopy.Api.Engines;

/// <summary>What to run to load a (decrypted, decompressed) backup back into the database.</summary>
public class RestoreCommand
{
    public IReadOnlyList<string> Argv { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
    /// <summary>Network mode: feed this local file to the process's stdin.</summary>
    public string? StdinFile { get; init; }
    /// <summary>Network mode: files to write (0600) before running and delete afterwards.</summary>
    public Dictionary<string, string> TempFiles { get; init; } = [];
    /// <summary>Network-mode SQLite: copy into this database file with the online backup API.</summary>
    public string? SqliteTargetPath { get; init; }
}

public static class RestoreCommandBuilder
{
    public static bool Supports(Engine e) => e != Engine.Redis;

    public static string? UnsupportedReason(BackupTarget t) => t.Engine switch
    {
        Engine.Redis => "Redis restores are not automated: stop the server, put the .rdb file in its data directory as dump.rdb, start it again.",
        Engine.Custom when string.IsNullOrWhiteSpace(t.RestoreCommand) => "Set a restore command for this custom target first.",
        _ => null,
    };

    /// <param name="file">Network: local path of the plain dump. Exec: path inside the container.</param>
    public static RestoreCommand Build(BackupTarget t, Credentials c, string file, string tempDir)
    {
        if (UnsupportedReason(t) is { } reason) throw new InvalidOperationException(reason);
        return t.Mode == ExecutionMode.Network ? BuildNetwork(t, c, file, tempDir) : BuildExec(t, c, file);
    }

    private static RestoreCommand BuildNetwork(BackupTarget t, Credentials c, string file, string tempDir)
    {
        var host = t.Host ?? "";
        var port = (t.Port ?? EngineCatalog.Get(t.Engine).DefaultPort ?? 0).ToString();
        switch (t.Engine)
        {
            case Engine.PostgreSql:
            {
                var argv = new List<string> { "psql", "-h", host, "-p", port, "-w", "-X", "-q" };
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["-U", c.User]);
                // A cluster dump (pg_dumpall) tolerates "role already exists"-style errors; a single database stops on the first one.
                if (string.IsNullOrEmpty(c.Database)) argv.AddRange(["-d", "postgres"]);
                else argv.AddRange(["-v", "ON_ERROR_STOP=1", "-d", c.Database]);
                argv.AddRange(["-f", file]);
                return new() { Argv = argv, Env = Pw("PGPASSWORD", c.Password) };
            }
            case Engine.MySql:
            case Engine.MariaDb:
            {
                var argv = new List<string> { "mariadb", "-h", host, "-P", port, "--protocol=TCP", "--skip-ssl-verify-server-cert" };
                if (!string.IsNullOrEmpty(c.User)) argv.Add($"--user={c.User}");
                if (!string.IsNullOrEmpty(c.Database)) argv.Add(c.Database);
                return new() { Argv = argv, Env = Pw("MYSQL_PWD", c.Password), StdinFile = file };
            }
            case Engine.MongoDb:
            {
                var argv = new List<string> { "mongorestore", "--host", host, "--port", port, $"--archive={file}", "--drop", "--quiet" };
                var temp = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["--username", c.User, "--authenticationDatabase", "admin"]);
                if (!string.IsNullOrEmpty(c.Password))
                {
                    var cfg = Path.Combine(tempDir, $"mongo-{Guid.NewGuid():N}.yaml");
                    temp[cfg] = $"password: \"{c.Password.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\n";
                    argv.AddRange(["--config", cfg]);
                }
                return new() { Argv = argv, TempFiles = temp };
            }
            case Engine.Sqlite:
                return new() { SqliteTargetPath = t.DatabaseName };
            case Engine.Custom:
                return new() { Argv = ["sh", "-c", t.RestoreCommand!], Env = CustomEnv(t, c, host, port), StdinFile = file };
            default:
                throw new InvalidOperationException($"{EngineCatalog.Get(t.Engine).Label} cannot be restored in network mode");
        }
    }

    private static RestoreCommand BuildExec(BackupTarget t, Credentials c, string file)
    {
        switch (t.Engine)
        {
            case Engine.PostgreSql:
            {
                var argv = new List<string> { "psql", "-w", "-X", "-q" };
                if (!string.IsNullOrEmpty(c.User)) argv.AddRange(["-U", c.User]);
                if (string.IsNullOrEmpty(c.Database)) argv.AddRange(["-d", "postgres"]);
                else argv.AddRange(["-v", "ON_ERROR_STOP=1", "-d", c.Database]);
                argv.AddRange(["-f", file]);
                return new() { Argv = argv, Env = Pw("PGPASSWORD", c.Password) };
            }
            case Engine.MySql:
            case Engine.MariaDb:
            {
                const string script = """
                    F=$1; shift
                    if command -v mariadb >/dev/null 2>&1; then exec mariadb "$@" < "$F"; fi
                    if command -v mysql >/dev/null 2>&1; then exec mysql "$@" < "$F"; fi
                    echo "neither mariadb nor mysql found in the container" >&2; exit 127
                    """;
                var argv = new List<string> { "sh", "-c", script, "sh", file };
                if (!string.IsNullOrEmpty(c.User)) argv.Add($"--user={c.User}");
                if (!string.IsNullOrEmpty(c.Database)) argv.Add(c.Database);
                return new() { Argv = argv, Env = Pw("MYSQL_PWD", c.Password) };
            }
            case Engine.MongoDb:
            {
                var args = new List<string> { $"--archive={file}", "--drop", "--quiet" };
                if (!string.IsNullOrEmpty(c.User)) args.AddRange(["--username", c.User, "--authenticationDatabase", "admin"]);
                const string script = """
                    if [ -n "$ACOPY_PASSWORD" ]; then exec mongorestore "$@" --password "$ACOPY_PASSWORD"; fi
                    exec mongorestore "$@"
                    """;
                return new() { Argv = ["sh", "-c", script, "sh", .. args], Env = Pw("ACOPY_PASSWORD", c.Password) };
            }
            case Engine.Sqlite:
            {
                const string script = """
                    command -v sqlite3 >/dev/null 2>&1 || { echo "sqlite3 is not available in the container — restore by copying the file while the app is stopped" >&2; exit 127; }
                    exec sqlite3 "$1" ".restore '$2'"
                    """;
                return new() { Argv = ["sh", "-c", script, "sh", t.DatabaseName!, file] };
            }
            case Engine.SqlServer:
            {
                var db = c.Database ?? throw new InvalidOperationException("SQL Server needs a database name");
                if (db.IndexOfAny([']', '\'', ';']) >= 0) throw new InvalidOperationException("Invalid SQL Server database name");
                const string script = """
                    S=$(command -v sqlcmd || ls /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd 2>/dev/null | head -n1)
                    [ -n "$S" ] || { echo "sqlcmd not found in the container" >&2; exit 127; }
                    case "$S" in *18*) TRUST=-C ;; *) TRUST= ;; esac
                    exec "$S" -S localhost -U "$1" $TRUST -b -Q "IF DB_ID(N'$2') IS NOT NULL ALTER DATABASE [$2] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$2] FROM DISK = N'$3' WITH REPLACE; ALTER DATABASE [$2] SET MULTI_USER;"
                    """;
                return new() { Argv = ["sh", "-c", script, "sh", c.User ?? "sa", db, file], Env = Pw("SQLCMDPASSWORD", c.Password) };
            }
            case Engine.Custom:
            {
                var env = CustomEnv(t, c, t.Host ?? "", t.Port?.ToString() ?? "");
                env["ACOPY_RESTORE_COMMAND"] = t.RestoreCommand!;
                return new() { Argv = ["sh", "-c", "exec sh -c \"$ACOPY_RESTORE_COMMAND\" < \"$1\"", "sh", file], Env = env };
            }
            default:
                throw new InvalidOperationException($"{EngineCatalog.Get(t.Engine).Label} cannot be restored automatically");
        }
    }

    private static Dictionary<string, string> CustomEnv(BackupTarget t, Credentials c, string host, string port) => new()
    {
        ["ACOPY_HOST"] = host,
        ["ACOPY_PORT"] = port,
        ["ACOPY_USER"] = c.User ?? "",
        ["ACOPY_PASSWORD"] = c.Password ?? "",
        ["ACOPY_DATABASE"] = c.Database ?? "",
        ["ACOPY_NAME"] = t.Name,
    };

    private static Dictionary<string, string> Pw(string key, string? password) =>
        string.IsNullOrEmpty(password) ? [] : new() { [key] = password };
}
