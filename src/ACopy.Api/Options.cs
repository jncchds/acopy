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
    /// <summary>
    /// Docker endpoint: <c>unix:///path</c> or <c>tcp://host:port</c> (e.g. a socket proxy). Empty = the
    /// standard DOCKER_HOST variable, then <see cref="Socket"/>.
    /// </summary>
    public string? Host { get; set; }
    /// <summary>tcp: use TLS (also on when DOCKER_TLS_VERIFY=1).</summary>
    public bool Tls { get; set; }
    /// <summary>tcp + TLS: directory with ca.pem, cert.pem, key.pem (default DOCKER_CERT_PATH).</summary>
    public string? CertPath { get; set; }
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

public class TelegramOptions
{
    /// <summary>
    /// Bot API server used when a destination or the notification settings leave the URL blank — e.g. a
    /// self-hosted <c>http://telegram-bot-api:8081</c>. Empty = the public https://api.telegram.org.
    /// </summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Self-hosted server only: a folder shared with the Bot API server (inside its data folder), as ACopy sees it.
    /// Each file is copied there part by part, sent by path and deleted again — instead of an HTTP upload.
    /// Applies to destinations that use <see cref="ApiUrl"/>. Needs <see cref="ServerUploadDir"/> too.
    /// </summary>
    public string? UploadDir { get; set; }

    /// <summary>The same folder as the Bot API server sees it, e.g. <c>/var/lib/telegram-bot-api/acopy-uploads</c>.</summary>
    public string? ServerUploadDir { get; set; }
}

public class BackupOptions
{
    public int MaxParallel { get; set; } = 2;
    /// <summary>Run-history rows kept per target (files are governed by Keep, not this).</summary>
    public int HistoryPerTarget { get; set; } = 200;
    public int LogMaxChars { get; set; } = 16_000;
}
