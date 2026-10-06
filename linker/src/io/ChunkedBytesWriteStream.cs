using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// A write-only stream that appends to a ChunkedBytes: what a MemoryStream
/// was for builders that write through a BinaryWriter, without the doubling
/// array (and the old one beside it while it doubles) and without the
/// ToArray copy at the end.
/// </summary>
public sealed class ChunkedBytesWriteStream : Stream
{
    private readonly ChunkedBytes bytes;
    private bool disposed;

    public ChunkedBytesWriteStream(ChunkedBytes bytes) { this.bytes = bytes; }
    public ChunkedBytes Bytes => bytes;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !disposed;
    public override long Length { get { EnsureOpen(); return bytes.Count; } }
    public override long Position
    {
        get { EnsureOpen(); return bytes.Count; }
        set => throw new NotSupportedException();
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureOpen();
        bytes.AddRange(new ReadOnlySpan<byte>(buffer, offset, count));
    }
    public override void Write(ReadOnlySpan<byte> buffer) { EnsureOpen(); bytes.AddRange(buffer); }
    public override void WriteByte(byte value) { EnsureOpen(); bytes.Add(value); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { EnsureOpen(); }
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
    private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(ChunkedBytesWriteStream)); }
}
