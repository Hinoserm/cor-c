using Corsac.Lang.Ir;
using Microsoft.Win32.SafeHandles;

namespace Corsac.Lang.Elf;

/// <summary>
/// A section's content as a read-only, seekable stream, from its chunks or
/// from the file it was left in (Section.FileBacked), read a buffer at a time.
/// What the note readers parse through a BinaryReader: given the content as
/// one array (Section.Content), a large unit's lifetime or region hints were
/// one contiguous request of megabytes each, made again for every unit.
/// </summary>
public sealed class SectionReadStream : Stream
{
    private const int BufferBytes = 64 * 1024;
    private readonly ChunkedBytes? _bytes;
    private readonly SafeFileHandle? _file;
    private readonly long _start;
    private readonly int _length;
    private byte[]? _buffer;
    private long _bufferAt = -1;
    private int _bufferCount;
    private long _position;
    private bool _disposed;

    public SectionReadStream(ChunkedBytes bytes) { _bytes = bytes; _length = bytes.Count; }

    /// <summary>The `length` bytes at `offset` of a file, which this stream opens and closes.</summary>
    public SectionReadStream(string path, long offset, int length)
    {
        _file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _start = offset;
        _length = length;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { EnsureOpen(); return _length; } }
    public override long Position
    {
        get { EnsureOpen(); return _position; }
        set { EnsureOpen(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        EnsureOpen();
        int taken = (int)Math.Min(buffer.Length, Math.Max(0L, _length - _position));
        if (taken == 0) return 0;
        if (_bytes is not null)
        {
            _bytes.CopyTo((int)_position, buffer, taken);
            _position += taken;
            return taken;
        }
        int done = 0;
        while (done < taken)
        {
            if (_bufferAt < 0 || _position < _bufferAt || _position >= _bufferAt + _bufferCount) Fill();
            int at = (int)(_position - _bufferAt);
            int n = Math.Min(taken - done, _bufferCount - at);
            new ReadOnlySpan<byte>(_buffer, at, n).CopyTo(buffer.Slice(done, n));
            done += n;
            _position += n;
        }
        return taken;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    /// <summary>The buffer refilled from the file at the current position.</summary>
    private void Fill()
    {
        _buffer ??= new byte[BufferBytes];
        int want = (int)Math.Min(BufferBytes, _length - _position);
        int done = 0;
        while (done < want)
        {
            int got = RandomAccess.Read(_file!, _buffer.AsSpan(done, want - done), _start + _position + done);
            if (got <= 0) throw new EndOfStreamException("section ends early in its file");
            done += got;
        }
        _bufferAt = _position;
        _bufferCount = want;
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

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing) _file?.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }

    private void EnsureOpen() { if (_disposed) throw new ObjectDisposedException(nameof(SectionReadStream)); }
}
