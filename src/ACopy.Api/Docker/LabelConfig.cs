using ACopy.Api.Data;
using ACopy.Api.Engines;
using ACopy.Api.Services;

namespace ACopy.Api.Docker;

/// <summary>A backup target described by container labels (<c>acopy.enable=true</c>, <c>acopy.engine=…</c>, …).</summary>
public record LabelConfig(
    string Key, string Name, Engine Engine, ExecutionMode Mode, string? Host, int? Port, string ContainerName,
    string? User, string? Password, string? PasswordEnv, string? Database, string? Args, string? Extension,
    string Schedule, int Keep, int? MaxAgeDays, NamingMode Naming, Compression Compression, string Folder, int TimeoutMinutes,
    string? EncryptionKey, IReadOnlyList<string> Destinations, string? RestoreCommand, string? TelegramChats = null)
{
    /// <summary>Every key ACopy reads, for the UI's label reference.</summary>
    public static readonly IReadOnlyList<(string Key, string Description)> Keys =
    [
        ("enable", "true to manage this container (required)"),
        ("name", "Display name and file base name (default: container name)"),
        ("engine", "postgresql | mysql | mariadb | mongodb | redis | sqlite | sqlserver | custom (default: detected from the image)"),
        ("mode", "exec (default — run the dump inside this container) | network"),
        ("schedule", "Cron expression, 5 or 6 fields (default: 0 3 * * *)"),
        ("keep", "Copies to keep / rolling slots (default: 7; 0 = unlimited when max_age_days is set)"),
        ("max_age_days", "Also delete copies older than this many days (timestamp naming)"),
        ("naming", "timestamp (default) | rolling"),
        ("compression", "gzip (default) | brotli | none"),
        ("database", "Database name (empty = all) or SQLite file path; comma-separated = one target per database"),
        ("user", "User (default: from the image's env, e.g. POSTGRES_USER)"),
        ("password_env", "Read the password from this env var of the container"),
        ("password", "Literal password (prefer password_env)"),
        ("encryption", "Name of an encryption key defined in ACopy"),
        ("destinations", "Comma-separated destination names to copy each backup to"),
        ("telegram_chats", "Comma-separated Telegram chat ids for this job's files, instead of each Telegram destination's chat"),
        ("host", "network mode: host (default: container name)"),
        ("port", "network mode: port (default: engine default)"),
        ("args", "Extra arguments for the dump tool; for custom: the shell command"),
        ("restore_command", "custom: shell command that receives the backup on stdin"),
        ("extension", "custom: output file extension"),
        ("folder", "Sub-folder under /backups (default: name)"),
        ("timeout", "Timeout in minutes (default: 120)"),
        ("jobs.<job>.<key>", "Several jobs on one container: each job inherits the keys above and overrides any of them"),
    ];

    /// <summary>
    /// Parses one container's labels into zero or more targets. With <c>acopy.jobs.&lt;job&gt;.*</c> labels each job is a
    /// target (inheriting the base keys); a comma-separated <c>database</c> further splits into one target per database.
    /// </summary>
    public static (List<LabelConfig> Configs, List<string> Errors) Parse(
        string prefix, IReadOnlyDictionary<string, string> labels, string containerName, string image)
    {
        var configs = new List<LabelConfig>();
        var errors = new List<string>();

        string? Base(string key) => labels.TryGetValue($"{prefix}.{key}", out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        if (!string.Equals(Base("enable"), "true", StringComparison.OrdinalIgnoreCase)) return (configs, errors);

        var jobsPrefix = $"{prefix}.jobs.";
        var jobs = labels.Keys
            .Where(k => k.StartsWith(jobsPrefix, StringComparison.Ordinal))
            .Select(k => k[jobsPrefix.Length..])
            .Where(rest => rest.Contains('.'))
            .Select(rest => rest[..rest.IndexOf('.')])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        var baseName = Base("name") ?? containerName;
        if (jobs.Count == 0)
        {
            ParseJob(Base, baseName, null);
        }
        else
        {
            foreach (var job in jobs)
            {
                string? Get(string key) =>
                    labels.TryGetValue($"{jobsPrefix}{job}.{key}", out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : Base(key);
                var explicitName = labels.TryGetValue($"{jobsPrefix}{job}.name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : null;
                if (string.Equals(Get("enable"), "false", StringComparison.OrdinalIgnoreCase)) continue;
                ParseJob(Get, explicitName ?? $"{baseName}-{job}", job);
            }
        }
        return (configs, errors);

        void ParseJob(Func<string, string?> L, string name, string? job)
        {
            var where = job == null ? "" : $"job '{job}': ";
            Engine engine;
            var engineLabel = L("engine");
            if (engineLabel != null)
            {
                var parsed = ParseEngine(engineLabel);
                if (parsed == null) { errors.Add($"{where}unknown engine '{engineLabel}'"); return; }
                engine = parsed.Value;
            }
            else
            {
                var detected = EngineCatalog.DetectFromImage(image);
                if (detected == null) { errors.Add($"{where}cannot detect the engine from image '{image}' — set {prefix}.engine"); return; }
                engine = detected.Value;
            }

            var mode = (L("mode") ?? "exec").ToLowerInvariant() switch
            {
                "network" or "net" or "tcp" => ExecutionMode.Network,
                _ => ExecutionMode.DockerExec,
            };
            if (mode == ExecutionMode.Network && !EngineCatalog.Get(engine).SupportsNetwork)
            { errors.Add($"{where}{EngineCatalog.Get(engine).Label} does not support network mode"); return; }

            var schedule = L("schedule") ?? "0 3 * * *";
            if (Scheduler.TryParse(schedule) == null) { errors.Add($"{where}invalid schedule '{schedule}'"); return; }

            int? maxAge = int.TryParse(L("max_age_days"), out var ma) && ma > 0 ? ma : null;
            var keep = int.TryParse(L("keep"), out var k) && k >= 0 ? Math.Min(k, 1000) : 7;
            var naming = string.Equals(L("naming"), "rolling", StringComparison.OrdinalIgnoreCase) ? NamingMode.Rolling : NamingMode.Timestamp;
            if (keep == 0 && (maxAge == null || naming == NamingMode.Rolling)) keep = 7;
            var compression = (L("compression") ?? "gzip").ToLowerInvariant() switch
            {
                "none" or "off" or "false" => Compression.None,
                "brotli" or "br" => Compression.Brotli,
                _ => Compression.Gzip,
            };
            int? port = int.TryParse(L("port"), out var p) ? p : null;
            var timeout = int.TryParse(L("timeout"), out var t) && t > 0 ? t : 120;
            var destinations = (L("destinations") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var chats = ACopy.Api.Destinations.TelegramClient.ParseChats(L("telegram_chats"));
            if (chats.FirstOrDefault(c => !ACopy.Api.Destinations.TelegramClient.IsValidChat(c)) is { } badChat)
            { errors.Add($"{where}telegram_chats: '{badChat}' is not a chat id"); return; }
            var telegramChats = chats.Count > 0 ? string.Join(',', chats) : null;

            // database=a,b → one target per database
            var dbLabel = L("database");
            List<string?> databases = engine != Engine.Sqlite && dbLabel != null && dbLabel.Contains(',')
                ? [.. dbLabel.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()]
                : [dbLabel];

            foreach (var db in databases)
            {
                var targetName = databases.Count > 1 ? $"{name}-{db}" : name;
                var folderLabel = L("folder");
                var folder = folderLabel == null ? targetName : databases.Count > 1 ? $"{folderLabel}/{db}" : folderLabel;
                configs.Add(new LabelConfig(
                    Key: targetName, Name: targetName, Engine: engine, Mode: mode,
                    Host: mode == ExecutionMode.Network ? L("host") ?? containerName : null, Port: port,
                    ContainerName: containerName,
                    User: L("user"), Password: L("password"), PasswordEnv: L("password_env"),
                    Database: db, Args: L("args") ?? L("command"), Extension: L("extension"),
                    Schedule: schedule, Keep: keep, MaxAgeDays: maxAge, Naming: naming, Compression: compression,
                    Folder: BackupFiles.SafeFolder(folder), TimeoutMinutes: timeout,
                    EncryptionKey: L("encryption"), Destinations: destinations, RestoreCommand: L("restore_command"),
                    TelegramChats: telegramChats));
            }
        }
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
