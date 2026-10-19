using ACopy.Api.Data;

namespace ACopy.Api.Engines;

public record EngineInfo(
    Engine Engine,
    string Label,
    int? DefaultPort,
    string Extension,
    bool SupportsNetwork,
    bool SupportsExec,
    /// <summary>An empty database name dumps everything (pg_dumpall, --all-databases, …).</summary>
    bool AllDatabasesWhenEmpty,
    string DatabaseHint,
    string? DefaultUser,
    string Notes);

public record DetectedCredentials(string? User, string? Password, string? Database);

public static class EngineCatalog
{
    public static readonly IReadOnlyList<EngineInfo> All =
    [
        new(Engine.PostgreSql, "PostgreSQL", 5432, "sql", true, true, true,
            "Database name — empty dumps every database (pg_dumpall)", "postgres",
            "Plain SQL via pg_dump / pg_dumpall. Restore with psql. Network mode uses the bundled pg_dump (new enough for any older server); exec mode uses the server's own."),
        new(Engine.MySql, "MySQL", 3306, "sql", true, true, true,
            "Database name — empty dumps every database (--all-databases)", "root",
            "SQL via mariadb-dump (network) or the container's mysqldump (exec), --single-transaction with routines, triggers and events."),
        new(Engine.MariaDb, "MariaDB", 3306, "sql", true, true, true,
            "Database name — empty dumps every database (--all-databases)", "root",
            "SQL via mariadb-dump, --single-transaction with routines, triggers and events."),
        new(Engine.MongoDb, "MongoDB", 27017, "archive", true, true, true,
            "Database name — empty dumps every database", null,
            "mongodump --archive. Restore with mongorestore --archive. Credentials authenticate against the admin database unless you pass --authenticationDatabase."),
        new(Engine.Redis, "Redis / Valkey", 6379, "rdb", true, true, false,
            "Not used", null,
            "RDB snapshot via redis-cli --rdb. Restore by placing the file as dump.rdb in the data directory."),
        new(Engine.Sqlite, "SQLite", null, "sqlite", true, true, false,
            "Path to the .sqlite file (inside ACopy for network mode, inside the target container for exec mode)", null,
            "Network mode = a file mounted into ACopy, copied with SQLite's online backup API (consistent). Exec mode uses sqlite3 .backup if the container has it, otherwise copies the file as-is."),
        new(Engine.SqlServer, "SQL Server", 1433, "bak", false, true, false,
            "Database name (required)", "sa",
            "Exec mode only: BACKUP DATABASE … WITH COPY_ONLY via the container's sqlcmd, streamed out. Restore with RESTORE DATABASE."),
        new(Engine.Custom, "Custom command", null, "bin", true, true, false,
            "Exposed to the command as $ACOPY_DATABASE", null,
            "Any shell command whose stdout is the backup. $ACOPY_HOST, $ACOPY_PORT, $ACOPY_USER, $ACOPY_PASSWORD and $ACOPY_DATABASE are set."),
    ];

    public static EngineInfo Get(Engine e) => All.First(x => x.Engine == e);

    /// <summary>Guess the engine from a container image reference (for label discovery and the container picker).</summary>
    public static Engine? DetectFromImage(string image)
    {
        var i = image.ToLowerInvariant();
        // Strip registry/namespace and tag: "docker.io/bitnami/postgresql:16" → "postgresql"
        var name = i.Split('@')[0];
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];

        if (name.Contains("postgis") || name.Contains("timescale") || name.Contains("postgres") || name == "pgvector") return Engine.PostgreSql;
        if (name.Contains("mariadb")) return Engine.MariaDb;
        if (name.Contains("mysql") || name.Contains("percona")) return Engine.MySql;
        if (name.Contains("mongo")) return Engine.MongoDb;
        if (name.Contains("redis") || name.Contains("valkey") || name.Contains("keydb")) return Engine.Redis;
        if (name.Contains("mssql") || (i.Contains("mssql") && name == "server")) return Engine.SqlServer;
        return null;
    }

    /// <summary>Credentials the official images take from their environment variables.</summary>
    public static DetectedCredentials DetectCredentials(Engine engine, IReadOnlyDictionary<string, string> env)
    {
        string? Get(params string[] keys) => keys.Select(k => env.TryGetValue(k, out var v) && v.Length > 0 ? v : null).FirstOrDefault(v => v != null);

        switch (engine)
        {
            case Engine.PostgreSql:
                return new(Get("POSTGRES_USER", "POSTGRESQL_USERNAME") ?? "postgres",
                           Get("POSTGRES_PASSWORD", "POSTGRESQL_PASSWORD"),
                           Get("POSTGRES_DB", "POSTGRESQL_DATABASE"));
            case Engine.MySql:
            case Engine.MariaDb:
                var root = Get("MARIADB_ROOT_PASSWORD", "MYSQL_ROOT_PASSWORD");
                if (root != null) return new("root", root, Get("MARIADB_DATABASE", "MYSQL_DATABASE"));
                return new(Get("MARIADB_USER", "MYSQL_USER"), Get("MARIADB_PASSWORD", "MYSQL_PASSWORD"),
                           Get("MARIADB_DATABASE", "MYSQL_DATABASE"));
            case Engine.MongoDb:
                return new(Get("MONGO_INITDB_ROOT_USERNAME", "MONGODB_ROOT_USER"),
                           Get("MONGO_INITDB_ROOT_PASSWORD", "MONGODB_ROOT_PASSWORD"), null);
            case Engine.Redis:
                return new(null, Get("REDIS_PASSWORD", "VALKEY_PASSWORD"), null);
            case Engine.SqlServer:
                return new("sa", Get("MSSQL_SA_PASSWORD", "SA_PASSWORD"), null);
            default:
                return new(null, null, null);
        }
    }

    public static string FileExtension(BackupTarget t)
    {
        var ext = t.Engine == Engine.Custom && !string.IsNullOrWhiteSpace(t.CustomExtension)
            ? t.CustomExtension.Trim().TrimStart('.')
            : Get(t.Engine).Extension;
        return t.Compression switch
        {
            Compression.Gzip => ext + ".gz",
            Compression.Brotli => ext + ".br",
            _ => ext,
        };
    }
}
