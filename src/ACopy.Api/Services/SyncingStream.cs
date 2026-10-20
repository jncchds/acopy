using Microsoft.Win32.SafeHandles;

namespace ACopy.Api.Services;

/// <summary>
/// Pass-through for a large file write that forces the file to disk each time another
/// <paramref name="every"/> bytes have reached it. Without this the kernel lets gigabytes of unwritten
/// ("dirty") pages pile up in memory and then flushes them all at once, which stalls every process on
/// the host that touches the disk. The file is watched through its own handle, so this also works when
/// another process (age) writes it. <paramref name="every"/> ≤ 0 turns syncing off.
/// </summary>
internal sealed class SyncingStream(Stream inner, string path, long every) : Stream
{
    private SafeFileHandle? _handle;
    private FileStream? _syncer;
    private long _synced;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        if (Due()) Sync();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        if (Due()) await Task.Run(Sync, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private bool Due()
    {
        if (every <= 0) return false;
        if (_handle == null)
        {
            // age creates the file itself, possibly only after its first input
            try { _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { return false; }
            _syncer = new FileStream(_handle, FileAccess.Write, bufferSize: 0);
        }
        return RandomAccess.GetLength(_handle) - _synced >= every;
    }

    private void Sync()
    {
        _synced = RandomAccess.GetLength(_handle!);
        _syncer!.Flush(flushToDisk: true); // fsync: applies to the file, whichever handle wrote it
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _syncer?.Dispose();
            _handle?.Dispose();
        }
        base.Dispose(disposing);
    }
}
