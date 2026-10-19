using System.Diagnostics;

namespace ACopy.Api.Services;

public record ToolVersion(string Tool, string? Version);

/// <summary>Versions of the bundled client tools (probed once, lazily).</summary>
public class SystemInfo
{
    private static readonly string[][] Probes =
    [
        ["pg_dump", "--version"],
        ["mariadb-dump", "--version"],
        ["mongodump", "--version"],
        ["redis-cli", "--version"],
        ["sqlite3", "--version"],
    ];

    private Task<List<ToolVersion>>? _tools;

    public Task<List<ToolVersion>> ToolsAsync() => _tools ??= Task.Run(async () =>
    {
        var list = new List<ToolVersion>();
        foreach (var p in Probes) list.Add(new(p[0], await ProbeAsync(p)));
        return list;
    });

    private static async Task<string?> ProbeAsync(string[] argv)
    {
        try
        {
            var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch { return null; }
    }
}
