using System.Buffers.Binary;
using System.Text;
using ACopy.Api.Data;
using ACopy.Api.Docker;
using ACopy.Api.Engines;
using ACopy.Api.Services;

namespace ACopy.Tests;

public class EngineTests
{
    [Theory]
    [InlineData("postgres:17-alpine", Engine.PostgreSql)]
    [InlineData("docker.io/postgis/postgis:16-3.4", Engine.PostgreSql)]
    [InlineData("timescale/timescaledb:latest-pg16", Engine.PostgreSql)]
    [InlineData("mariadb:11", Engine.MariaDb)]
    [InlineData("mysql:8.4", Engine.MySql)]
    [InlineData("bitnami/mongodb:7.0", Engine.MongoDb)]
    [InlineData("valkey/valkey:8", Engine.Redis)]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", Engine.SqlServer)]
    public void DetectFromImage(string image, Engine expected) => Assert.Equal(expected, EngineCatalog.DetectFromImage(image));

    [Fact]
    public void DetectFromImage_Unknown() => Assert.Null(EngineCatalog.DetectFromImage("nginx:latest"));

    [Fact]
    public void DetectCredentials_MySqlPrefersRoot()
    {
        var c = EngineCatalog.DetectCredentials(Engine.MySql, new Dictionary<string, string>
        {
            ["MYSQL_ROOT_PASSWORD"] = "r", ["MYSQL_USER"] = "u", ["MYSQL_PASSWORD"] = "p", ["MYSQL_DATABASE"] = "d",
        });
        Assert.Equal(new DetectedCredentials("root", "r", "d"), c);
    }

    [Fact]
    public void SplitArgs_HandlesQuotes() =>
        Assert.Equal(["--exclude-table", "a b", "-x", "it's", "c\"d"], DumpCommandBuilder.SplitArgs("--exclude-table 'a b' -x \"it's\" c\\\"d"));

    [Fact]
    public void Network_PgDump_PutsPasswordInEnvOnly()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.PostgreSql, Mode = ExecutionMode.Network, Host = "db", ExtraArgs = "--no-owner" };
        var cmd = DumpCommandBuilder.Build(t, new Credentials("app", "s3cret", "appdb"), "/tmp");
        Assert.Equal(["pg_dump", "-h", "db", "-p", "5432", "-w", "-d", "appdb", "-U", "app", "--no-owner"], cmd.Argv);
        Assert.Equal("s3cret", cmd.Env["PGPASSWORD"]);
        Assert.DoesNotContain(cmd.Argv, a => a.Contains("s3cret"));
    }

    [Fact]
    public void Network_EmptyDatabase_UsesDumpAll()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.PostgreSql, Mode = ExecutionMode.Network, Host = "db" };
        Assert.Equal("pg_dumpall", DumpCommandBuilder.Build(t, new Credentials(null, null, null), "/tmp").Argv[0]);
    }

    [Fact]
    public void Network_Mongo_PasswordGoesToConfigFile()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.MongoDb, Mode = ExecutionMode.Network, Host = "m" };
        var cmd = DumpCommandBuilder.Build(t, new Credentials("root", "p\"w", null), "/tmp");
        Assert.DoesNotContain(cmd.Argv, a => a.Contains("p\"w"));
        Assert.Single(cmd.TempFiles);
        Assert.Contains("password: \"p\\\"w\"", cmd.TempFiles.Values.Single());
    }

    [Fact]
    public void Exec_SqlServer_RejectsInjection()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.SqlServer, Mode = ExecutionMode.DockerExec, ContainerName = "x" };
        Assert.Throws<InvalidOperationException>(() => DumpCommandBuilder.Build(t, new Credentials("sa", "p", "x]; DROP"), "/tmp"));
    }

    [Fact]
    public void SqlServer_NetworkUnsupported()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.SqlServer, Mode = ExecutionMode.Network, Host = "h" };
        Assert.Throws<InvalidOperationException>(() => DumpCommandBuilder.Build(t, new Credentials("sa", "p", "db"), "/tmp"));
    }

    [Fact]
    public void Labels_Parse_DefaultsAndDetection()
    {
        var (cfg, err) = LabelConfig.Parse("acopy", new Dictionary<string, string>
        {
            ["acopy.enable"] = "true", ["acopy.keep"] = "4", ["acopy.naming"] = "rolling", ["acopy.schedule"] = "*/30 * * * *",
        }, "app-db", "postgres:17");
        Assert.Null(err);
        Assert.NotNull(cfg);
        Assert.Equal(Engine.PostgreSql, cfg.Engine);
        Assert.Equal(ExecutionMode.DockerExec, cfg.Mode);
        Assert.Equal(NamingMode.Rolling, cfg.Naming);
        Assert.Equal(4, cfg.Keep);
        Assert.Equal("app-db", cfg.Name);
        Assert.Equal(Compression.Gzip, cfg.Compression);
    }

    [Fact]
    public void Labels_NotEnabled_IsIgnored()
    {
        var (cfg, err) = LabelConfig.Parse("acopy", new Dictionary<string, string> { ["acopy.engine"] = "postgres" }, "x", "postgres");
        Assert.Null(cfg);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("acopy.schedule", "not a cron", "invalid schedule")]
    [InlineData("acopy.engine", "oracle", "unknown engine")]
    public void Labels_Errors(string key, string value, string expected)
    {
        var (cfg, err) = LabelConfig.Parse("acopy", new Dictionary<string, string> { ["acopy.enable"] = "true", [key] = value }, "x", "postgres");
        Assert.Null(cfg);
        Assert.Contains(expected, err);
    }

    [Fact]
    public void Cron_FiveAndSixFields()
    {
        Assert.NotNull(Scheduler.TryParse("0 3 * * *"));
        Assert.NotNull(Scheduler.TryParse("30 0 3 * * *"));
        Assert.Null(Scheduler.TryParse("every day"));
        var next = Scheduler.NextOccurrence("0 3 * * *", new DateTimeOffset(2026, 10, 19, 4, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public async Task Docker_Demultiplex_SplitsStreams()
    {
        static byte[] Frame(byte type, string payload)
        {
            var p = Encoding.UTF8.GetBytes(payload);
            var f = new byte[8 + p.Length];
            f[0] = type;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(4), (uint)p.Length);
            p.CopyTo(f, 8);
            return f;
        }
        var body = new MemoryStream([.. Frame(1, "hello "), .. Frame(2, "warn"), .. Frame(1, "world")]);
        var stdout = new MemoryStream();
        var stderr = new StringBuilder();
        await DockerClient.Demultiplex(body, stdout, stderr, CancellationToken.None);
        Assert.Equal("hello world", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal("warn", stderr.ToString());
    }
}
