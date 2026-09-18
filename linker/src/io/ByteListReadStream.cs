namespace Corsac.Lang.Elf;

/// <summary>A seekable, non-owning view of an object section without cloning its bytes.</summary>
public sealed class ByteListReadStream : Stream
{
    private readonly IReadOnlyList<byte> bytes;
    private long position;
    private bool disposed;

    public ByteListReadStream(IReadOnlyList<byte> bytes) { this.bytes = bytes; }
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length { get { EnsureOpen(); return bytes.Count; } }
    public override long Position
    {
        get { EnsureOpen(); return position; }
        set { EnsureOpen(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); position = value; }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        int taken = (int)Math.Min(count, Math.Max(0L, bytes.Count - position));
        for (int i = 0; i < taken; i++) buffer[offset + i] = bytes[(int)position + i];
        position += taken;
        return taken;
    }
    public override int ReadByte()
    {
        EnsureOpen();
        return position < bytes.Count ? bytes[(int)position++] : -1;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long basis = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position,
            SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        long next = checked(basis + offset);
        if (next < 0) throw new IOException("Cannot seek before the section");
        Position = next;
        return next;
    }
    public override void Flush() { EnsureOpen(); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
    private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(ByteListReadStream)); }
}
