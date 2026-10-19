using ACopy.Api.Data;
using ACopy.Api.Engines;
using ACopy.Api.Services;

namespace ACopy.Api.Endpoints;

public record RunDto(
    long Id, int TargetId, string? TargetName, string Status, string Trigger,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? FilePath, long? SizeBytes, string? Sha256,
    int? Slot, bool FileGone, string? Error, string? Log)
{
    public static RunDto From(BackupRun r, bool includeLog = true) => new(
        r.Id, r.TargetId, r.Target?.Name, r.Status.ToString(), r.Trigger.ToString(), r.StartedAt, r.FinishedAt,
        r.FilePath, r.SizeBytes, r.Sha256, r.Slot, r.FileGone, r.Error, includeLog ? r.Log : null);
}

public record TargetDto(
    int Id, string Name, string Engine, string Mode, string? Host, int? Port, string? ContainerName,
    string? Username, bool HasPassword, string? PasswordEnv, string? DatabaseName, string? ExtraArgs, string? CustomExtension,
    string Schedule, bool Enabled, int Keep, string Naming, string Compression, string Folder, int TimeoutMinutes,
    string Source, bool Orphaned, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? NextRunAt, bool Active, RunDto? LastRun, DateTimeOffset? LastSuccessAt, long StoredBytes, int StoredFiles)
{
    public static TargetDto From(BackupTarget t, DateTimeOffset? next, bool active, RunDto? last, DateTimeOffset? lastOk, long bytes, int files) => new(
        t.Id, t.Name, t.Engine.ToString(), t.Mode.ToString(), t.Host, t.Port, t.ContainerName,
        t.Username, !string.IsNullOrEmpty(t.PasswordProtected), t.PasswordEnv, t.DatabaseName, t.ExtraArgs, t.CustomExtension,
        t.Schedule, t.Enabled, t.Keep, t.Naming.ToString(), t.Compression.ToString(), t.Folder, t.TimeoutMinutes,
        t.Source.ToString(), t.Orphaned, t.CreatedAt, t.UpdatedAt, next, active, last, lastOk, bytes, files);
}

public record TargetInput(
    string Name, Engine Engine, ExecutionMode Mode, string? Host, int? Port, string? ContainerName,
    string? Username, string? Password, bool? ClearPassword, string? PasswordEnv, string? DatabaseName, string? ExtraArgs,
    string? CustomExtension, string Schedule, bool Enabled, int Keep, NamingMode Naming, Compression Compression,
    string? Folder, int? TimeoutMinutes)
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
        if (Keep is < 1 or > 1000) e["keep"] = ["Keep must be 1–1000"];
        if (TimeoutMinutes is < 1 or > 24 * 60) e["timeoutMinutes"] = ["Timeout must be 1–1440 minutes"];
        if (CustomExtension != null && CustomExtension.Trim().TrimStart('.').Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.'))
            e["customExtension"] = ["Letters, digits and dots only"];
        return e;
    }
}

public record SettingsInput(string TimeZone, string? WebhookUrl, bool NotifyOnSuccess);
public record PasswordInput(string Password);
public record ChangePasswordInput(string Current, string Password);
public record EnabledInput(bool Enabled);
