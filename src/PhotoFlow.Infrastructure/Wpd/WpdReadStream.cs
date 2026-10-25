using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace PhotoFlow.Infrastructure.Wpd;

[SupportedOSPlatform("windows")]
internal sealed class WpdReadStream(ComWorker worker, IStream native) : Stream
{
    private bool disposed;
    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.Length == 0) return 0;
        var bytes = new byte[Math.Min(buffer.Length, 128 * 1024)];
        var read = await worker.InvokeAsync(() =>
        {
            var count = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                Marshal.WriteInt32(count, 0);
                native.Read(bytes, bytes.Length, count);
                var length = Marshal.ReadInt32(count);
                if (length < 0 || length > bytes.Length) throw new InvalidDataException("Некорректный размер WPD-потока.");
                return length;
            }
            finally { Marshal.FreeCoTaskMem(count); }
        }, ct);
        bytes.AsMemory(0, read).CopyTo(buffer);
        return read;
    }
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            worker.InvokeAsync(() => { WpdNative.Release(native); return true; }).GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
