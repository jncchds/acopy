using ACopy.Api.Data;
using ACopy.Api.Engines;

namespace ACopy.Tests;

public class EventSchedulerTests
{
    [Fact]
    public void RecognisesError1577()
    {
        Assert.True(DumpCommandBuilder.IsEventSchedulerDisabled(
            "dump exited with code 2: mariadb-dump: Couldn't execute 'show events': Cannot proceed, because event scheduler is disabled (1577)"));
        Assert.False(DumpCommandBuilder.IsEventSchedulerDisabled("mariadb-dump: Got error: 1045: Access denied for user 'root'"));
    }

    [Theory]
    [InlineData(ExecutionMode.Network)]
    [InlineData(ExecutionMode.DockerExec)]
    public void WithoutEvents_DropsOnlyThatOption(ExecutionMode mode)
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.MariaDb, Mode = mode, Host = "db", ContainerName = "db" };
        var cmd = DumpCommandBuilder.Build(t, new Credentials("root", "pw", "app"), "/tmp");
        var retry = DumpCommandBuilder.WithoutEvents(cmd);

        Assert.NotNull(retry);
        Assert.Contains("--events", cmd.Argv);
        Assert.DoesNotContain("--events", retry.Argv);
        Assert.Equal(cmd.Argv.Where(a => a != "--events"), retry.Argv);
        Assert.Equal(cmd.Env, retry.Env);
    }

    [Fact]
    public void WithoutEvents_NullForOtherEngines()
    {
        var t = new BackupTarget { Name = "n", Folder = "n", Engine = Engine.PostgreSql, Mode = ExecutionMode.Network, Host = "db" };
        Assert.Null(DumpCommandBuilder.WithoutEvents(DumpCommandBuilder.Build(t, new Credentials(null, null, "app"), "/tmp")));
    }
}
