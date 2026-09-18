using Corsac.Lang.Elf;

namespace Corsac.Tests.Elf;

public static class ByteListReadStreamTests
{
    public static void Run()
    {
        using ByteListReadStream stream = new(new List<byte> { 10, 20, 30 });
        byte[] buffer = new byte[4];
        if (stream.Read(buffer, 1, 2) != 2 || buffer[1] != 10 || buffer[2] != 20)
            throw new Exception("Section view read range failed");
        if (stream.ReadByte() != 30 || stream.ReadByte() != -1) throw new Exception("Section view EOF failed");
        stream.Seek(-2, SeekOrigin.End);
        if (stream.ReadByte() != 20) throw new Exception("Section view seek failed");
        stream.Position = 100;
        if (stream.Read(buffer, 0, 4) != 0) throw new Exception("Section view past-end read failed");
        stream.Dispose();
        try { stream.ReadByte(); } catch (ObjectDisposedException) { return; }
        throw new Exception("Disposed section view accepted a read");
    }
}
