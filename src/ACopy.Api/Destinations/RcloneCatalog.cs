namespace ACopy.Api.Destinations;

public record BackendField(
    string Key, string Label, bool Secret = false, bool Obscure = false, bool Required = false,
    string? Placeholder = null, string[]? Options = null, string? Hint = null);

public record BackendInfo(string Type, string Label, string PathHint, IReadOnlyList<BackendField> Fields);

/// <summary>
/// The rclone backends offered with a form in the UI. Anything else works through "other" — free-form rclone
/// options. <c>Obscure</c> fields are passed through <c>rclone obscure</c>, as rclone expects for passwords.
/// </summary>
public static class RcloneCatalog
{
    public static readonly IReadOnlyList<BackendInfo> Backends =
    [
        new("s3", "S3 / MinIO / R2 / Wasabi", "bucket/prefix",
        [
            new("provider", "Provider", Required: true, Options: ["AWS", "Minio", "Cloudflare", "Wasabi", "DigitalOcean", "Backblaze", "Ceph", "Other"]),
            new("access_key_id", "Access key ID", Required: true),
            new("secret_access_key", "Secret access key", Secret: true, Required: true),
            new("region", "Region", Placeholder: "us-east-1"),
            new("endpoint", "Endpoint", Placeholder: "https://minio.example.com — empty for AWS"),
            new("force_path_style", "Path-style URLs", Options: ["", "true", "false"], Hint: "MinIO usually needs true"),
        ]),
        new("b2", "Backblaze B2", "bucket/prefix",
        [
            new("account", "Application key ID", Required: true),
            new("key", "Application key", Secret: true, Required: true),
        ]),
        new("sftp", "SFTP", "/remote/directory",
        [
            new("host", "Host", Required: true),
            new("port", "Port", Placeholder: "22"),
            new("user", "User", Required: true),
            new("pass", "Password", Secret: true, Obscure: true),
            new("key_pem", "Private key (PEM)", Secret: true, Hint: "Paste the key; newlines are kept"),
        ]),
        new("webdav", "WebDAV / Nextcloud", "/remote.php/dav/files/USER/backups — or a path under the URL",
        [
            new("url", "URL", Required: true, Placeholder: "https://cloud.example.com/remote.php/dav/files/me"),
            new("vendor", "Vendor", Options: ["nextcloud", "owncloud", "sharepoint", "fastmail", "other"]),
            new("user", "User"),
            new("pass", "Password", Secret: true, Obscure: true),
        ]),
        new("smb", "SMB / Windows share", "share/folder",
        [
            new("host", "Host", Required: true),
            new("user", "User"),
            new("pass", "Password", Secret: true, Obscure: true),
            new("domain", "Domain", Placeholder: "WORKGROUP"),
        ]),
        new("ftp", "FTP", "/remote/directory",
        [
            new("host", "Host", Required: true),
            new("port", "Port", Placeholder: "21"),
            new("user", "User"),
            new("pass", "Password", Secret: true, Obscure: true),
            new("explicit_tls", "Explicit TLS", Options: ["", "true", "false"]),
        ]),
        new("local", "Local / mounted disk", "/mnt/offsite (mounted into the ACopy container)", []),
        new("other", "Other rclone backend", "path on the remote",
        [
            new("type", "rclone type", Required: true, Placeholder: "drive, onedrive, dropbox, azureblob, …"),
            new("options", "Options (key = value per line)", Hint: "Values for passwords must already be obscured (rclone obscure)"),
            new("secret_options", "Secret options (key = value per line)", Secret: true),
        ]),
    ];

    public static BackendInfo? Get(string? type) => Backends.FirstOrDefault(b => b.Type == type);
}
