using ACopy.Api.Data;
using ACopy.Api.Destinations;
using ACopy.Api.Engines;
using ACopy.Api.Services;

namespace ACopy.Api.Endpoints;

public record UploadDto(long Id, int DestinationId, string? DestinationName, string Kind, string Status, string? RemoteRef, string? Error,
    DateTimeOffset? FinishedAt, bool Deleted, bool CanRestore)
{
    public static UploadDto From(RunUpload u) => new(
        u.Id, u.DestinationId, u.Destination?.Name, u.Destination?.Kind.ToString() ?? "", u.Status.ToString(),
        u.Destination?.Kind == DestinationKind.Telegram ? null : u.RemoteRef, u.Error, u.FinishedAt, u.Deleted,
        u.Status == UploadStatus.Succeeded && !u.Deleted && u.Destination?.Kind == DestinationKind.Rclone);
}

public record RunDto(
    long Id, int TargetId, string? TargetName, string Kind, string Status, string Trigger, long? SourceRunId,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? FilePath, long? SizeBytes, string? Sha256,
    int? Slot, bool FileGone, int? EncryptionKeyId, string? Error, string? Log, IReadOnlyList<UploadDto> Uploads)
{
    public static RunDto From(BackupRun r, bool includeLog = true) => new(
        r.Id, r.TargetId, r.Target?.Name, r.Kind.ToString(), r.Status.ToString(), r.Trigger.ToString(), r.SourceRunId,
        r.StartedAt, r.FinishedAt, r.FilePath, r.SizeBytes, r.Sha256, r.Slot, r.FileGone, r.EncryptionKeyId,
        r.Error, includeLog ? r.Log : null, r.Uploads.OrderBy(u => u.Id).Select(UploadDto.From).ToList());
}

public record TargetDto(
    int Id, string Name, string Engine, string Mode, string? Host, int? Port, string? ContainerName,
    string? Username, bool HasPassword, string? PasswordEnv, string? DatabaseName, string? ExtraArgs, string? CustomExtension,
    string Schedule, bool Enabled, int Keep, int? MaxAgeDays, string Naming, string Compression, string Folder, int TimeoutMinutes,
    int? EncryptionKeyId, string? EncryptionKeyName, IReadOnlyList<int> DestinationIds, string? TelegramChatIds, string? RestoreCommand,
    string? RestoreUnsupported, string Source, bool Orphaned, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? NextRunAt, bool Active, RunDto? LastRun, DateTimeOffset? LastSuccessAt, long StoredBytes, int StoredFiles)
{
    public static TargetDto From(BackupTarget t, DateTimeOffset? next, bool active, RunDto? last, DateTimeOffset? lastOk, long bytes, int files) => new(
        t.Id, t.Name, t.Engine.ToString(), t.Mode.ToString(), t.Host, t.Port, t.ContainerName,
        t.Username, !string.IsNullOrEmpty(t.PasswordProtected), t.PasswordEnv, t.DatabaseName, t.ExtraArgs, t.CustomExtension,
        t.Schedule, t.Enabled, t.Keep, t.MaxAgeDays, t.Naming.ToString(), t.Compression.ToString(), t.Folder, t.TimeoutMinutes,
        t.EncryptionKeyId, t.EncryptionKey?.Name, t.Destinations.Select(d => d.DestinationId).Order().ToList(), t.TelegramChatIds, t.RestoreCommand,
        RestoreCommandBuilder.UnsupportedReason(t),
        t.Source.ToString(), t.Orphaned, t.CreatedAt, t.UpdatedAt, next, active, last, lastOk, bytes, files);
}

public record TargetInput(
    string Name, Engine Engine, ExecutionMode Mode, string? Host, int? Port, string? ContainerName,
    string? Username, string? Password, bool? ClearPassword, string? PasswordEnv, string? DatabaseName, string? ExtraArgs,
    string? CustomExtension, string Schedule, bool Enabled, int Keep, int? MaxAgeDays, NamingMode Naming, Compression Compression,
    string? Folder, int? TimeoutMinutes, int? EncryptionKeyId, List<int>? DestinationIds, string? RestoreCommand,
    string? TelegramChatIds = null)
{
    public Dictionary<string, string[]> Validate()
    {
        var e = new Dictionary<string, string[]>();
        var info = EngineCatalog.Get(Engine);
        if (string.IsNullOrWhiteSpace(Name)) e["name"] = ["Name is required"];
        else if (Name.Length > 100) e["name"] = ["Name is too long"];
        if (Mode == ExecutionMode.Network && !info.SupportsNetwork) e["mode"] = [$"{info.Label} supports Docker exec mode only"];
        if (Mode == ExecutionMode.DockerExec && !info.SupportsExec) e["mode"] = [$"{info.Label} does not support Docker exec mode"];
        if (Mode == ExecutionMode.Network && Engine is not (Engine.Sqlite or Engine.Custom) && string.IsNullOrWhiteSpace(Host)) e["host"] = ["Host is required"];
        if (Mode == ExecutionMode.DockerExec && string.IsNullOrWhiteSpace(ContainerName)) e["containerName"] = ["Container is required"];
        if (Port is < 1 or > 65535) e["port"] = ["Port must be 1–65535"];
        if (Engine is Engine.Sqlite && string.IsNullOrWhiteSpace(DatabaseName)) e["databaseName"] = ["The SQLite file path is required"];
        if (Engine is Engine.SqlServer && string.IsNullOrWhiteSpace(DatabaseName)) e["databaseName"] = ["Database name is required"];
        if (Engine is Engine.Custom && string.IsNullOrWhiteSpace(ExtraArgs)) e["extraArgs"] = ["The command is required"];
        if (Scheduler.TryParse(Schedule) == null) e["schedule"] = ["Not a valid cron expression"];
        if (Keep is < 0 or > 1000) e["keep"] = ["Keep must be 0–1000"];
        else if (Keep == 0 && (Naming == NamingMode.Rolling || MaxAgeDays is null or < 1)) e["keep"] = ["0 (unlimited) needs timestamp naming and a maximum age"];
        if (MaxAgeDays is < 1 or > 36500) e["maxAgeDays"] = ["Maximum age must be 1–36500 days"];
        if (TimeoutMinutes is < 1 or > 24 * 60) e["timeoutMinutes"] = ["Timeout must be 1–1440 minutes"];
        if (CustomExtension != null && CustomExtension.Trim().TrimStart('.').Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.'))
            e["customExtension"] = ["Letters, digits and dots only"];
        if (TelegramClient.ParseChats(TelegramChatIds).FirstOrDefault(c => !TelegramClient.IsValidChat(c)) is { } badChat)
            e["telegramChatIds"] = [$"'{badChat}' is not a chat id — use numbers (e.g. -1001234567890) or @channel, separated by commas"];
        return e;
    }
}

public record KeyDto(int Id, string Name, string Kind, bool HasSecret, IReadOnlyList<string> Recipients, int UsedBy, DateTimeOffset CreatedAt, string DecryptHint);

/// <param name="Generate">age: let ACopy create the key pair (identity stored, exportable).</param>
public record KeyInput(string Name, KeyKind Kind, string? Passphrase, bool? Generate, string? Recipients, string? Identity);

public record DestinationDto(
    int Id, string Name, string Kind, bool Enabled, string? RcloneType, Dictionary<string, string> Params,
    IReadOnlyList<string> SecretKeys, string? RemotePath, int? Keep, int? MaxAgeDays, int UsedBy, DateTimeOffset CreatedAt);

/// <param name="Secrets">New secret values; a key with an empty value keeps the stored one, null in <paramref name="ClearSecrets"/> removes it.</param>
public record DestinationInput(
    string Name, DestinationKind Kind, bool Enabled, string? RcloneType, Dictionary<string, string>? Params,
    Dictionary<string, string>? Secrets, List<string>? ClearSecrets, string? RemotePath, int? Keep, int? MaxAgeDays);

public record RestoreInput(bool SafetyBackup, string ConfirmName, string? AgeIdentity, long? FromUploadId);

public record SettingsInput(
    string TimeZone, string? WebhookUrl, bool NotifyOnSuccess,
    string? TelegramToken, bool? ClearTelegramToken, string? TelegramChatId, string? TelegramThreadId, string? TelegramApiUrl);
public record PasswordInput(string Password);
public record ChangePasswordInput(string Current, string Password);
public record EnabledInput(bool Enabled);
