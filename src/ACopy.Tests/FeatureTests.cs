using System.Text;
using ACopy.Api.Data;
using ACopy.Api.Destinations;
using ACopy.Api.Docker;
using ACopy.Api.Engines;
using ACopy.Api.Services;

namespace ACopy.Tests;

public class RetentionAgeTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MaxAge_DeletesOld_ButKeepsNewest()
    {
        var files = new[]
        {
            new FileEntry("db_20261019-030000.sql.gz", Now.AddDays(-1)),
            new FileEntry("db_20261010-030000.sql.gz", Now.AddDays(-10)),
            new FileEntry("db_20260901-030000.sql.gz", Now.AddDays(-49)),
        };
        var pruned = BackupFiles.FilesToPrune("db", NamingMode.Timestamp, new RetentionPolicy(0, 7), files, null, Now);
        Assert.Equal(["db_20261010-030000.sql.gz", "db_20260901-030000.sql.gz"], pruned);

        var allOld = files.Select(f => f with { ModifiedUtc = Now.AddDays(-100) });
        Assert.DoesNotContain("db_20261019-030000.sql.gz", BackupFiles.FilesToPrune("db", NamingMode.Timestamp, new RetentionPolicy(0, 7), allOld, null, Now));
    }

    [Fact]
    public void CountAndAge_Combine()
    {
        var files = Enumerable.Range(0, 5).Select(i => new FileEntry($"db_2026101{9 - i}-030000.sql", Now.AddDays(-i))).ToList();
        var pruned = BackupFiles.FilesToPrune("db", NamingMode.Timestamp, new RetentionPolicy(4, 2), files, null, Now);
        Assert.Equal(["db_20261016-030000.sql", "db_20261015-030000.sql"], pruned);
    }
}

public class LabelJobsTests
{
    [Fact]
    public void CommaSeparatedDatabases_SplitIntoTargets()
    {
        var (cfgs, errs) = LabelConfig.Parse("acopy", new Dictionary<string, string>
        {
            ["acopy.enable"] = "true", ["acopy.database"] = "app, analytics", ["acopy.destinations"] = "s3, tg",
            ["acopy.encryption"] = "main", ["acopy.max_age_days"] = "30",
        }, "pg", "postgres:17");
        Assert.Empty(errs);
        Assert.Equal(["pg-app", "pg-analytics"], cfgs.Select(c => c.Name));
        Assert.Equal(["pg-app", "pg-analytics"], cfgs.Select(c => c.Folder));
        Assert.All(cfgs, c => Assert.Equal(["s3", "tg"], c.Destinations));
        Assert.All(cfgs, c => Assert.Equal("main", c.EncryptionKey));
        Assert.All(cfgs, c => Assert.Equal(30, c.MaxAgeDays));
    }

    [Fact]
    public void Jobs_InheritAndOverride()
    {
        var (cfgs, errs) = LabelConfig.Parse("acopy", new Dictionary<string, string>
        {
            ["acopy.enable"] = "true", ["acopy.keep"] = "5", ["acopy.schedule"] = "0 3 * * *",
            ["acopy.jobs.hourly.schedule"] = "0 * * * *", ["acopy.jobs.hourly.naming"] = "rolling", ["acopy.jobs.hourly.keep"] = "24",
            ["acopy.jobs.nightly.database"] = "app",
            ["acopy.jobs.off.enable"] = "false",
        }, "pg", "postgres:17");
        Assert.Empty(errs);
        Assert.Equal(["pg-hourly", "pg-nightly"], cfgs.Select(c => c.Name));
        Assert.Equal("0 * * * *", cfgs[0].Schedule);
        Assert.Equal(NamingMode.Rolling, cfgs[0].Naming);
        Assert.Equal(24, cfgs[0].Keep);
        Assert.Equal("0 3 * * *", cfgs[1].Schedule);
        Assert.Equal(5, cfgs[1].Keep);
        Assert.Equal("app", cfgs[1].Database);
    }

    [Fact]
    public void JobErrors_AreNamed()
    {
        var (cfgs, errs) = LabelConfig.Parse("acopy", new Dictionary<string, string>
        {
            ["acopy.enable"] = "true", ["acopy.jobs.bad.schedule"] = "nope", ["acopy.jobs.good.keep"] = "3",
        }, "pg", "postgres:17");
        Assert.Single(cfgs);
        Assert.Contains("job 'bad'", errs.Single());
    }
}

public class EncryptionTests
{
    [Fact]
    public void Passphrase_RoundTrip_HasOpenSslHeader()
    {
        var plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("select 1; -- ", 5000)));
        var cipher = new MemoryStream();
        using (var enc = Encryption.PassphraseEncrypt(cipher, "correct horse battery staple")) enc.Write(plain);
        var bytes = cipher.ToArray();
        Assert.Equal("Salted__", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(0, (bytes.Length - 16) % 16);

        using var dec = Encryption.PassphraseDecrypt(new MemoryStream(bytes), "correct horse battery staple");
        var back = new MemoryStream();
        dec.CopyTo(back);
        Assert.Equal(plain, back.ToArray());
    }

    [Fact]
    public void Passphrase_WrongKey_Fails()
    {
        var cipher = new MemoryStream();
        using (var enc = Encryption.PassphraseEncrypt(cipher, "right passphrase")) enc.Write("hello"u8);
        Assert.ThrowsAny<Exception>(() =>
        {
            using var dec = Encryption.PassphraseDecrypt(new MemoryStream(cipher.ToArray()), "wrong passphrase!");
            dec.CopyTo(Stream.Null);
        });
    }

    [Fact]
    public void Recipients_Validation()
    {
        Assert.True(Encryption.IsValidRecipient("age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p"));
        Assert.True(Encryption.IsValidRecipient("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI me@host"));
        Assert.False(Encryption.IsValidRecipient("hello"));
        Assert.Equal(["a", "b"], Encryption.ParseRecipients("# comment\na\n\n b \n"));
    }

    [Fact]
    public void FileName_GetsEncryptionSuffix()
    {
        var t = new BackupTarget { Name = "db", Folder = "db", Engine = Engine.PostgreSql, Compression = Compression.Gzip };
        Assert.Equal("db_20261019-030000.sql.gz.age", BackupFiles.FileName(t, new DateTime(2026, 10, 19, 3, 0, 0), 0, KeyKind.Age));
        Assert.Equal("db_20261019-030000.sql.gz.enc", BackupFiles.FileName(t, new DateTime(2026, 10, 19, 3, 0, 0), 0, KeyKind.Passphrase));
    }
}

public class RestoreAndDestinationTests
{
    [Fact]
    public void Postgres_SingleDb_StopsOnError()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.PostgreSql, Mode = ExecutionMode.Network, Host = "db" };
        var c = RestoreCommandBuilder.Build(t, new Credentials("u", "p", "app"), "/tmp/x.sql", "/tmp");
        Assert.Contains("ON_ERROR_STOP=1", c.Argv);
        Assert.Equal("p", c.Env["PGPASSWORD"]);
        Assert.DoesNotContain(c.Argv, a => a == "p");
    }

    [Fact]
    public void MySql_Network_UsesStdin()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.MySql, Mode = ExecutionMode.Network, Host = "db" };
        var c = RestoreCommandBuilder.Build(t, new Credentials("root", "pw", "shop"), "/tmp/x.sql", "/tmp");
        Assert.Equal("/tmp/x.sql", c.StdinFile);
        Assert.Equal("shop", c.Argv[^1]);
    }

    [Fact]
    public void Redis_IsNotAutomated()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.Redis, Mode = ExecutionMode.DockerExec, ContainerName = "r" };
        Assert.NotNull(RestoreCommandBuilder.UnsupportedReason(t));
        Assert.Throws<InvalidOperationException>(() => RestoreCommandBuilder.Build(t, new Credentials(null, null, null), "/tmp/x", "/tmp"));
    }

    [Fact]
    public void Custom_NeedsRestoreCommand()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.Custom, Mode = ExecutionMode.Network, ExtraArgs = "cat x" };
        Assert.NotNull(RestoreCommandBuilder.UnsupportedReason(t));
        t.RestoreCommand = "cat > y";
        Assert.Null(RestoreCommandBuilder.UnsupportedReason(t));
    }

    [Fact]
    public void RcloneOptionLines_Parse()
    {
        var parsed = DestinationService.ParseOptionLines("# c\nclient_id = abc\n bad line\ntoken={\"x\":1}\n").ToList();
        Assert.Equal([("client_id", "abc"), ("token", "{\"x\":1}")], parsed);
    }

    [Fact]
    public void RemotePaths_Join()
    {
        static string Base(string? p) => DestinationService.RemoteBase(new Destination { Name = "x", RemotePath = p });
        Assert.Equal("ACOPY:bucket/pre/db/f.gz", DestinationService.Join(DestinationService.Join(Base("bucket/pre/"), "db"), "f.gz"));
        Assert.Equal("ACOPY:/mnt/x/db", DestinationService.Join(Base("/mnt/x"), "db"));
        Assert.Equal("ACOPY:db", DestinationService.Join(Base(null), "db"));
    }
}
