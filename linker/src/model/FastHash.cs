#nullable enable

namespace Corsac.Lang.Ir;

/// <summary>
/// A 32-BYTE CHECK OF BYTES, NOT A CRYPTOGRAPHIC ONE: for what the compiler
/// and the linker write beside data only to notice that the data is not what
/// it was -- an IR archive's records and directory, the native object they
/// were archived beside, a function's code the link may fold a call to, a
/// source file against the index made from it. SHA-256 there was a fifth of
/// a native unit compile's time; nothing here defends against a person
/// choosing bytes to collide, which nothing that reads these values needs.
///
/// Four 32-bit lanes over 16-byte stripes, as xxHash32 runs them -- a
/// multiply, a rotate and a multiply a word, all 32-bit, so a 486 runs it
/// without a 64-bit operation -- then each lane, the length and the others
/// mixed into one word of eight. The same bytes, however they are handed
/// in, give the same value.
/// </summary>
public sealed class FastHash
{
    private const uint P1 = 2654435761u, P2 = 2246822519u, P3 = 3266489917u, P4 = 668265263u, P5 = 374761393u;

    private uint _a = unchecked(0x9E3779B1u + P2), _b = P2, _c = 0, _d = unchecked(0u - P1);
    private readonly byte[] _held = new byte[16];
    private int _heldCount;
    private long _length;

    public static byte[] Of(ReadOnlySpan<byte> data)
    {
        FastHash hash = new();
        hash.Append(data);
        return hash.Finish();
    }

    public static byte[] Of(byte[] data) => Of(new ReadOnlySpan<byte>(data));

    public void Append(byte[] data, int offset, int count) => Append(new ReadOnlySpan<byte>(data, offset, count));

    public void Append(ReadOnlySpan<byte> data)
    {
        _length += data.Length;
        int at = 0;
        if (_heldCount > 0)
        {
            int take = Math.Min(16 - _heldCount, data.Length);
            data[..take].CopyTo(_held.AsSpan(_heldCount));
            _heldCount += take;
            at = take;
            if (_heldCount < 16) return;
            Stripe(_held, 0);
            _heldCount = 0;
        }
        uint a = _a, b = _b, c = _c, d = _d;
        for (; at + 16 <= data.Length; at += 16)
        {
            a = Lane(a, Word(data, at));
            b = Lane(b, Word(data, at + 4));
            c = Lane(c, Word(data, at + 8));
            d = Lane(d, Word(data, at + 12));
        }
        _a = a; _b = b; _c = c; _d = d;
        if (at < data.Length)
        {
            data[at..].CopyTo(_held);
            _heldCount = data.Length - at;
        }
    }

    private void Stripe(byte[] stripe, int at)
    {
        ReadOnlySpan<byte> s = stripe;
        _a = Lane(_a, Word(s, at));
        _b = Lane(_b, Word(s, at + 4));
        _c = Lane(_c, Word(s, at + 8));
        _d = Lane(_d, Word(s, at + 12));
    }

    private static uint Word(ReadOnlySpan<byte> s, int at) => (uint)(s[at] | s[at + 1] << 8 | s[at + 2] << 16 | s[at + 3] << 24);

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

    private static uint Lane(uint lane, uint word) => Rotl(lane + word * P2, 13) * P1;

    private static uint Avalanche(uint h)
    {
        h ^= h >> 15; h *= P2;
        h ^= h >> 13; h *= P3;
        h ^= h >> 16;
        return h;
    }

    /// <summary>The 32 bytes: each lane and seven mixes of them with the length and the tail.</summary>
    public byte[] Finish()
    {
        uint tail = P5;
        for (int i = 0; i < _heldCount; i++) tail = Rotl(tail ^ (_held[i] * P5), 11) * P1;
        uint length = (uint)_length, high = (uint)(_length >> 32);
        uint[] words =
        {
            Avalanche(_a ^ length ^ tail), Avalanche(_b + high + Rotl(tail, 7)), Avalanche(_c ^ Rotl(length, 13) ^ P3), Avalanche(_d + tail * P4),
            Avalanche(Rotl(_a, 1) + Rotl(_b, 7) + Rotl(_c, 12) + Rotl(_d, 18) + length),
            Avalanche(_a * P3 ^ _c * P4 ^ tail), Avalanche(_b * P4 + _d * P3 + high), Avalanche(_a + _b + _c + _d + tail + length),
        };
        byte[] made = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            made[i * 4] = (byte)words[i];
            made[i * 4 + 1] = (byte)(words[i] >> 8);
            made[i * 4 + 2] = (byte)(words[i] >> 16);
            made[i * 4 + 3] = (byte)(words[i] >> 24);
        }
        return made;
    }
}
