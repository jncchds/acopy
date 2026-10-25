using ACopy.Api.Data;
using ACopy.Api.Engines;

namespace ACopy.Tests;

public class DumpLoadTests
{
    private static BackupTarget Mongo(ExecutionMode mode, string? args = null) => new()
    {
        Name = "m", Folder = "m", Engine = Engine.MongoDb, Mode = mode, Host = "mongo", ContainerName = "mongo", ExtraArgs = args,
    };

    [Theory]
    [InlineData(ExecutionMode.Network)]
    [InlineData(ExecutionMode.DockerExec)]
    public void Mongo_DumpsOneCollectionAtATime(ExecutionMode mode)
    {
        var cmd = DumpCommandBuilder.Build(Mongo(mode), new Credentials("root", "pw", null), "/tmp");
        Assert.Contains("--numParallelCollections=1", cmd.Argv);
    }

    [Theory]
    [InlineData("--numParallelCollections=4")]
    [InlineData("-j 2")]
    public void Mongo_UserParallelismWins(string args)
    {
        var cmd = DumpCommandBuilder.Build(Mongo(ExecutionMode.DockerExec, args), new Credentials(null, null, null), "/tmp");
        Assert.DoesNotContain("--numParallelCollections=1", cmd.Argv);
    }

    [Fact]
    public void LowPriority_WrapsCommandAndKeepsArguments()
    {
        string[] argv = ["sh", "-c", "exec mongodump \"$@\"", "sh", "--archive"];
        var wrapped = DumpCommandBuilder.LowPriority(argv, 10);

        Assert.Equal(["sh", "-c"], wrapped.Take(2));
        Assert.Equal("10", wrapped[4]);
        Assert.Equal(argv, wrapped.Skip(5));
    }

    [Fact]
    public void LowPriority_ZeroLeavesCommandAlone()
    {
        string[] argv = ["mongodump", "--archive"];
        Assert.Same(argv, DumpCommandBuilder.LowPriority(argv, 0));
    }

    [Fact]
    public void LowPriority_RunsAtLowerPriority()
    {
        if (OperatingSystem.IsWindows()) return;
        var wrapped = DumpCommandBuilder.LowPriority(["sh", "-c", "cut -d' ' -f19 /proc/self/stat"], 7);
        var psi = new System.Diagnostics.ProcessStartInfo(wrapped[0]) { RedirectStandardOutput = true };
        foreach (var a in wrapped.Skip(1)) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var niceness = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();

        Assert.Equal(0, p.ExitCode);
        Assert.Equal((7 + int.Parse(File.ReadAllText("/proc/self/stat").Split(' ')[18])).ToString(), niceness);
    }
}
