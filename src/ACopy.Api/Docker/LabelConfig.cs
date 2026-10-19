using ACopy.Api.Data;
using ACopy.Api.Engines;
using ACopy.Api.Services;

namespace ACopy.Api.Docker;

/// <summary>A backup target described by container labels (<c>acopy.enable=true</c>, <c>acopy.engine=…</c>, …).</summary>
public record LabelConfig(
    string Key, string Name, Engine Engine, ExecutionMode Mode, string? Host, int? Port, string ContainerName,
    string? User, string? Password, string? PasswordEnv, string? Database, string? Args, string? Extension,
    string Schedule, int Keep, NamingMode Naming, Compression Compression, string Folder, int TimeoutMinutes)
{
    /// <summary>Every key ACopy reads, for the UI's label reference.</summary>
    public static readonly IReadOnlyList<(string Key, string Description)> Keys =
    [
        ("enable", "true to manage this container (required)"),
        ("name", "Display name and file base name (default: container name)"),
        ("engine", "postgresql | mysql | mariadb | mongodb | redis | sqlite | sqlserver | custom (default: detected from the image)"),
        ("mode", "exec (default — run the dump inside this container) | network"),
        ("schedule", "Cron expression, 5 or 6 fields (default: 0 3 * * *)"),
        ("keep", "Copies to keep / rolling slots (default: 7)"),
        ("naming", "timestamp (default) | rolling"),
        ("compression", "gzip (default) | brotli | none"),
        ("database", "Database name (empty = all) or SQLite file path"),
        ("user", "User (default: from the image's env, e.g. POSTGRES_USER)"),
        ("password_env", "Read the password from this env var of the container"),
        ("password", "Literal password (prefer password_env)"),
        ("host", "network mode: host (default: container name)"),
        ("port", "network mode: port (default: engine default)"),
        ("args", "Extra arguments for the dump tool; for custom: the shell command"),
        ("extension", "custom: output file extension"),
        ("folder", "Sub-folder under /backups (default: name)"),
        ("timeout", "Timeout in minutes (default: 120)"),
    ];

    public static (LabelConfig? Config, string? Error) Parse(
        string prefix, IReadOnlyDictionary<string, string> labels, string containerName, string image)
    {
        string? L(string key) => labels.TryGetValue($"{prefix}.{key}", out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        if (!string.Equals(L("enable"), "true", StringComparison.OrdinalIgnoreCase)) return (null, null);

        var name = L("name") ?? containerName;

        Engine engine;
        var engineLabel = L("engine");
        if (engineLabel != null)
        {
            var parsed = ParseEngine(engineLabel);
            if (parsed == null) return (null, $"unknown engine '{engineLabel}'");
            engine = parsed.Value;
        }
        else
        {
            var detected = EngineCatalog.DetectFromImage(image);
            if (detected == null) return (null, $"cannot detect the engine from image '{image}' — set {prefix}.engine");
            engine = detected.Value;
        }

        var mode = (L("mode") ?? "exec").ToLowerInvariant() switch
        {
            "network" or "net" or "tcp" => ExecutionMode.Network,
            _ => ExecutionMode.DockerExec,
        };
        if (mode == ExecutionMode.Network && !EngineCatalog.Get(engine).SupportsNetwork)
            return (null, $"{EngineCatalog.Get(engine).Label} does not support network mode");

        var schedule = L("schedule") ?? "0 3 * * *";
        if (Scheduler.TryParse(schedule) == null) return (null, $"invalid schedule '{schedule}'");

        var keep = int.TryParse(L("keep"), out var k) && k > 0 ? Math.Min(k, 1000) : 7;
        var naming = string.Equals(L("naming"), "rolling", StringComparison.OrdinalIgnoreCase) ? NamingMode.Rolling : NamingMode.Timestamp;
        var compression = (L("compression") ?? "gzip").ToLowerInvariant() switch
        {
            "none" or "off" or "false" => Compression.None,
            "brotli" or "br" => Compression.Brotli,
            _ => Compression.Gzip,
        };
        int? port = int.TryParse(L("port"), out var p) ? p : null;
        var timeout = int.TryParse(L("timeout"), out var t) && t > 0 ? t : 120;

        return (new LabelConfig(
            Key: name, Name: name, Engine: engine, Mode: mode,
            Host: mode == ExecutionMode.Network ? L("host") ?? containerName : null, Port: port,
            ContainerName: containerName,
            User: L("user"), Password: L("password"), PasswordEnv: L("password_env"),
            Database: L("database"), Args: L("args") ?? L("command"), Extension: L("extension"),
            Schedule: schedule, Keep: keep, Naming: naming, Compression: compression,
            Folder: BackupFiles.SafeFolder(L("folder") ?? name), TimeoutMinutes: timeout), null);
    }

    public static Engine? ParseEngine(string s) => s.Trim().ToLowerInvariant() switch
    {
        "postgres" or "postgresql" or "pg" or "postgis" or "timescaledb" => Engine.PostgreSql,
        "mysql" => Engine.MySql,
        "mariadb" => Engine.MariaDb,
        "mongo" or "mongodb" => Engine.MongoDb,
        "redis" or "valkey" or "keydb" => Engine.Redis,
        "sqlite" or "sqlite3" => Engine.Sqlite,
        "mssql" or "sqlserver" or "sql-server" => Engine.SqlServer,
        "custom" => Engine.Custom,
        _ => null,
    };
}
