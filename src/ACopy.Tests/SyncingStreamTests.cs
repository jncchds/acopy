using ACopy.Api.Services;

namespace ACopy.Tests;

public class SyncingStreamTests
{
    [Fact]
    public async Task PassesEverythingThrough_AndReleasesTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acopy-sync-{Guid.NewGuid():N}");
        try
        {
            var data = new byte[3 * 1024 * 1024 + 123];
            new Random(1).NextBytes(data);
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16, useAsync: true))
            await using (var synced = new SyncingStream(file, path, 1024 * 1024))
            {
                for (var i = 0; i < data.Length; i += 81920)
                    await synced.WriteAsync(data.AsMemory(i, Math.Min(81920, data.Length - i)));
            }
            Assert.Equal(data, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FileNotThereYet_IsNotAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acopy-sync-{Guid.NewGuid():N}");
        await using var sink = new MemoryStream();
        await using (var synced = new SyncingStream(sink, path, 1))
            await synced.WriteAsync(new byte[10]);
        Assert.Equal(10, sink.Length);
    }
}
