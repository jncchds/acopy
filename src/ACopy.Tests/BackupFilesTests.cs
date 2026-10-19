using ACopy.Api.Data;
using ACopy.Api.Services;

namespace ACopy.Tests;

public class BackupFilesTests
{
    private static BackupTarget Target(NamingMode naming, int keep, Compression c = Compression.Gzip) => new()
    {
        Name = "My App DB", Folder = "my-app-db", Engine = Engine.PostgreSql, Naming = naming, Keep = keep, Compression = c,
    };

    private static readonly DateTime Now = new(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc);
    private static IEnumerable<FileEntry> Entries(IEnumerable<string> names) => names.Select(n => new FileEntry(n, Now));

    [Theory]
    [InlineData("My App DB", "my-app-db")]
    [InlineData("  Ünïcode / slashes\\ ", "n-code-slashes")]
    [InlineData("../../etc", "etc")]
    [InlineData("***", "backup")]
    public void Slug(string input, string expected) => Assert.Equal(expected, BackupFiles.Slug(input));

    [Fact]
    public void SafeFolder_StripsTraversal() => Assert.Equal("a/b", BackupFiles.SafeFolder("/../a/./b/.."));

    [Fact]
    public void FileName_Timestamp() =>
        Assert.Equal("my-app-db_20261019-030000.sql.gz", BackupFiles.FileName(Target(NamingMode.Timestamp, 3), new DateTime(2026, 10, 19, 3, 0, 0), 0));

    [Fact]
    public void FileName_Rolling() =>
        Assert.Equal("my-app-db.2.sql", BackupFiles.FileName(Target(NamingMode.Rolling, 4, Compression.None), DateTime.Now, 2));

    [Fact]
    public void TempName_IsIgnoredBySyncthing()
    {
        var tmp = BackupFiles.TempName("x.sql.gz");
        Assert.StartsWith(".syncthing.", tmp);
        Assert.EndsWith(".tmp", tmp);
        Assert.True(BackupFiles.IsAcopyTemp(tmp));
    }

    [Fact]
    public void Prune_Timestamp_KeepsNewestN_AndIgnoresForeignFiles()
    {
        var files = new[]
        {
            "my-app-db_20261015-030000.sql.gz",
            "my-app-db_20261016-030000.sql.gz",
            "my-app-db_20261017-030000.sql", // older compression setting — still ours
            "my-app-db_20261018-030000.sql.gz",
            "my-app-db_20261019-030000.sql.gz",
            "my-app-db-other_20261001-030000.sql.gz",
            "notes.txt",
        };
        var pruned = BackupFiles.FilesToPrune(Target(NamingMode.Timestamp, 3), Entries(files), "my-app-db_20261019-030000.sql.gz", Now);
        Assert.Equal(["my-app-db_20261016-030000.sql.gz", "my-app-db_20261015-030000.sql.gz"], pruned);
    }

    [Fact]
    public void Prune_Rolling_RemovesSlotsBeyondKeep_AndStaleExtensions()
    {
        var files = new[] { "my-app-db.0.sql.gz", "my-app-db.1.sql.gz", "my-app-db.1.sql", "my-app-db.2.sql.gz", "my-app-db.5.sql.gz", "other.0.sql.gz" };
        var pruned = BackupFiles.FilesToPrune(Target(NamingMode.Rolling, 2), Entries(files), "my-app-db.1.sql.gz", Now);
        Assert.Equal(["my-app-db.1.sql", "my-app-db.2.sql.gz", "my-app-db.5.sql.gz"], pruned);
    }

    [Fact]
    public void OwnedFiles_MatchesOnlyThisTarget()
    {
        var files = new[] { "my-app-db_20261015-030000.sql.gz", "my-app-db.0.sql", "my-app-db2_20261015-030000.sql.gz", "x" };
        Assert.Equal(["my-app-db_20261015-030000.sql.gz"], BackupFiles.OwnedFiles(Target(NamingMode.Timestamp, 1), files));
    }

    [Fact]
    public void Password_RoundTrip()
    {
        var h = SettingsStore.HashPassword("correct horse");
        Assert.True(SettingsStore.VerifyPassword("correct horse", h));
        Assert.False(SettingsStore.VerifyPassword("wrong", h));
        Assert.False(SettingsStore.VerifyPassword("x", null));
    }
}
