namespace ACopy.Api;

/// <summary>Bound from the "ACopy" section; every key is overridable as ACOPY_Section__Key.</summary>
public class PathsOptions
{
    public string Database { get; set; } = "/data/acopy.db";
    public string Backups { get; set; } = "/backups";
}

public class DockerOptions
{
    public bool Enabled { get; set; } = true;
    public string Socket { get; set; } = "/var/run/docker.sock";
    public string LabelPrefix { get; set; } = "acopy";
    public int ScanSeconds { get; set; } = 60;
}

public class AuthOptions
{
    /// <summary>Turn off the login screen entirely (e.g. behind an authenticating reverse proxy).</summary>
    public bool Disabled { get; set; }
    /// <summary>When set, this password is (re)applied on every start — first-run setup and recovery.</summary>
    public string? Password { get; set; }
}

public class BackupOptions
{
    public int MaxParallel { get; set; } = 2;
    /// <summary>Run-history rows kept per target (files are governed by Keep, not this).</summary>
    public int HistoryPerTarget { get; set; } = 200;
    public int LogMaxChars { get; set; } = 16_000;
}
