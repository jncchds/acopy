namespace ACopy.Api.Data;

/// <summary>Database flavors ACopy knows how to dump.</summary>
public enum Engine
{
    PostgreSql,
    MySql,
    MariaDb,
    MongoDb,
    Redis,
    Sqlite,
    SqlServer,
    Custom,
}

/// <summary>Where the dump tool runs.</summary>
public enum ExecutionMode
{
    /// <summary>The client tool runs inside the ACopy container and connects over the network.</summary>
    Network,
    /// <summary>The dump tool runs inside the target container via the Docker socket (docker exec).</summary>
    DockerExec,
}

/// <summary>How backup files are named.</summary>
public enum NamingMode
{
    /// <summary><c>name_20261019-030000.sql.gz</c> — a new file every run; the oldest beyond Keep are deleted.</summary>
    Timestamp,
    /// <summary><c>name.0.sql.gz</c> → <c>name.1…</c> → back to 0 — a fixed set of files that get overwritten.</summary>
    Rolling,
}

public enum Compression
{
    None,
    Gzip,
    Brotli,
}

public enum RunKind
{
    Backup,
    Restore,
}

public enum KeyKind
{
    /// <summary>OpenSSL-compatible AES-256-CBC + PBKDF2 — decrypt anywhere with `openssl enc -d`.</summary>
    Passphrase,
    /// <summary>age public-key encryption — only the private key (identity) can decrypt.</summary>
    Age,
}

public enum DestinationKind
{
    /// <summary>Any rclone backend: S3 / MinIO / B2 / SFTP / WebDAV / SMB / FTP / local disk / …</summary>
    Rclone,
    /// <summary>Files sent to a Telegram chat by a bot (split into parts above the size limit).</summary>
    Telegram,
}

public enum UploadStatus
{
    Pending,
    Succeeded,
    Failed,
}

public enum TargetSource
{
    Manual,
    DockerLabel,
}

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum RunTrigger
{
    Schedule,
    Manual,
    /// <summary>The automatic backup taken right before a restore.</summary>
    SafetyBackup,
}

public class BackupTarget
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public Engine Engine { get; set; }
    public ExecutionMode Mode { get; set; }

    // Connection (Network mode)
    public string? Host { get; set; }
    public int? Port { get; set; }

    // Container (DockerExec mode) — the name, not the id, so it survives container re-creation
    public string? ContainerName { get; set; }

    public string? Username { get; set; }
    /// <summary>Encrypted with ASP.NET Core Data Protection. Never returned to the browser.</summary>
    public string? PasswordProtected { get; set; }
    /// <summary>DockerExec: read the password from this environment variable of the target container.</summary>
    public string? PasswordEnv { get; set; }
    /// <summary>Database name; empty = every database (where the engine supports it). For SQLite: the file path.</summary>
    public string? DatabaseName { get; set; }
    /// <summary>Extra arguments appended to the dump tool; for <see cref="Engine.Custom"/> the full shell command.</summary>
    public string? ExtraArgs { get; set; }
    /// <summary>File extension for <see cref="Engine.Custom"/> output (without the dot, e.g. "tar").</summary>
    public string? CustomExtension { get; set; }

    /// <summary>Five-field cron expression (minute hour day month weekday), evaluated in the configured time zone.</summary>
    public string Schedule { get; set; } = "0 3 * * *";
    public bool Enabled { get; set; } = true;
    /// <summary>How many copies to keep. In Rolling mode this is the number of slots (0…Keep-1).</summary>
    public int Keep { get; set; } = 7;
    /// <summary>Also delete copies older than this many days (timestamp naming). The newest copy is always kept.</summary>
    public int? MaxAgeDays { get; set; }
    public NamingMode Naming { get; set; } = NamingMode.Timestamp;
    public Compression Compression { get; set; } = Compression.Gzip;
    /// <summary>Sub-folder under the backups root. Defaults to a slug of the name.</summary>
    public required string Folder { get; set; }
    public int TimeoutMinutes { get; set; } = 120;

    /// <summary>Encrypt files with this key (null = plain).</summary>
    public int? EncryptionKeyId { get; set; }
    public EncryptionKey? EncryptionKey { get; set; }
    /// <summary>Custom engine: shell command that receives the (decrypted, decompressed) backup on stdin.</summary>
    public string? RestoreCommand { get; set; }
    public List<TargetDestination> Destinations { get; set; } = [];
    /// <summary>
    /// Comma-separated Telegram chat ids this job's files go to, in place of each Telegram destination's own chat
    /// (e.g. the people responsible for this database). Null = the destinations' chats.
    /// </summary>
    public string? TelegramChatIds { get; set; }

    /// <summary>Next rolling slot to write.</summary>
    public int NextSlot { get; set; }

    public TargetSource Source { get; set; } = TargetSource.Manual;
    /// <summary>For label-managed targets: the stable key (acopy.name label or container name).</summary>
    public string? LabelKey { get; set; }
    /// <summary>Label-managed target whose container is gone. Kept for its history; deletable.</summary>
    public bool Orphaned { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<BackupRun> Runs { get; set; } = [];
}

public class BackupRun
{
    public long Id { get; set; }
    public int TargetId { get; set; }
    public BackupTarget? Target { get; set; }

    public RunKind Kind { get; set; } = RunKind.Backup;
    /// <summary>Restore runs: the backup run whose file was restored.</summary>
    public long? SourceRunId { get; set; }
    public RunStatus Status { get; set; }
    public RunTrigger Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Path relative to the backups root.</summary>
    public string? FilePath { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public int? Slot { get; set; }
    /// <summary>The file has since been rotated out, overwritten, or deleted.</summary>
    public bool FileGone { get; set; }
    /// <summary>Key the file was encrypted with (null = not encrypted).</summary>
    public int? EncryptionKeyId { get; set; }
    public List<RunUpload> Uploads { get; set; } = [];

    public string? Error { get; set; }
    /// <summary>Tool stderr / diagnostics, truncated.</summary>
    public string? Log { get; set; }
}

public class AppSetting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}

public class EncryptionKey
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public KeyKind Kind { get; set; }
    /// <summary>Passphrase, or the age identity (private key) — Data Protection–encrypted. Optional for age.</summary>
    public string? SecretProtected { get; set; }
    /// <summary>age recipients (public keys), one per line.</summary>
    public string? Recipients { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A remote place backups are copied to after a successful local backup.</summary>
public class Destination
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DestinationKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>rclone backend type (s3, sftp, webdav, …); unused for Telegram.</summary>
    public string? RcloneType { get; set; }
    /// <summary>Non-secret parameters as a JSON object (rclone options, or Telegram chat id / thread / API URL).</summary>
    public string ParamsJson { get; set; } = "{}";
    /// <summary>Secret parameters as a JSON object, Data Protection–encrypted.</summary>
    public string? SecretsProtected { get; set; }
    /// <summary>rclone: base path on the remote (bucket/prefix, directory, …). Each target uploads into its folder below it.</summary>
    public string? RemotePath { get; set; }
    /// <summary>Remote retention override; null = same as the target's local retention.</summary>
    public int? Keep { get; set; }
    public int? MaxAgeDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TargetDestination
{
    public int TargetId { get; set; }
    public BackupTarget? Target { get; set; }
    public int DestinationId { get; set; }
    public Destination? Destination { get; set; }
}

public class RunUpload
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public BackupRun? Run { get; set; }
    public int DestinationId { get; set; }
    public Destination? Destination { get; set; }
    public UploadStatus Status { get; set; }
    /// <summary>rclone: remote file path. Telegram: <c>chat:id,id;chat:id</c> (see <c>TelegramRef</c>).</summary>
    public string? RemoteRef { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Removed from the remote by retention (or found missing).</summary>
    public bool Deleted { get; set; }
}
