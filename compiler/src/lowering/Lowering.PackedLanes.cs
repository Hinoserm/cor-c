#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

/// <summary>
/// A PACKED OPERATION WHERE THE VARIANT HAS NO INSTRUCTION FOR IT, written
/// out in place: its lanes one at a time, each loaded from its operands at
/// its own width, worked as an integer or a float, and stored at its own
/// width into the result -- the code a 486's own loop would be, which the
/// optimiser then works on as any other (Lowering.PackedIntrinsic: a
/// variant without MMX, a Packed128's lanes across its halves without SSE2,
/// a float operation 3DNow! has not). No call and no packing between
/// operations: a saturation is a compare and a select, as the 486, which
/// has no CMOV, does it in a register. The results are the instructions'
/// to the bit (PADDSW's clamp, PMULHW's high half, CVTTPS2DQ's 0x80000000).
/// </summary>
public sealed partial class Lowering
{
    private VReg LaneConst(long v) => _e.Const(v, IrType.I32);

    /// <summary>Lane `k` of `bits` bits at `at`: an I32 sign- or zero-extended, or an I64 of 64.</summary>
    private VReg LaneAt(VReg at, int bits, int k, bool signed)
        => bits == 64 ? _e.Load(IrType.I64, at, 8 * k) : _e.Load(IrType.I32, at, k * bits / 8, bits / 8, signed);

    private void LanePut(VReg at, int bits, int k, VReg v) => _e.Store(RegOperand.Of(at), RegOperand.Of(v), k * bits / 8, bits / 8);

    /// <summary>`yes` where `cond` (0 or 1) is 1, else `no`: a mask, no branch.</summary>
    private VReg LaneChoose(VReg cond, VReg yes, VReg no)
    {
        VReg mask = _e.Unary(Opcode.Neg, cond);
        return _e.Binary(Opcode.Xor, no, _e.Binary(Opcode.And, _e.Binary(Opcode.Xor, yes, no), mask));
    }

    private VReg LaneCompare(Opcode op, VReg x, VReg y) => _e.Binary(op, R(x), R(y), IrType.I32);

    private VReg LaneClamp(VReg v, long least, long most)
    {
        VReg low = LaneConst(least), high = LaneConst(most);
        v = LaneChoose(LaneCompare(Opcode.LtS, v, low), low, v);
        return LaneChoose(LaneCompare(Opcode.GtS, v, high), high, v);
    }

    /// <summary>
    /// An integer operation over `bytes` (8, or 16 for a Packed128's lanes
    /// across its halves) of `a` and `b` into `made`, as the instruction
    /// whose opcode `op` is: lane-wise, or a pack or an unpack over the
    /// whole width.
    /// </summary>
    private void LanesBinary(byte op, VReg made, VReg a, VReg b, int bytes)
    {
        switch (op)
        {
            case 0xD4: case 0xFB:
                for (int k = 0; k < bytes / 8; k++)
                    LanePut(made, 64, k, _e.Binary(op == 0xD4 ? Opcode.Add : Opcode.Sub, LaneAt(a, 64, k, false), LaneAt(b, 64, k, false)));
                return;
            case 0xDB: case 0xEB: case 0xEF: case 0xDF:
                for (int k = 0; k < bytes / 4; k++)
                {
                    VReg x = LaneAt(a, 32, k, false), y = LaneAt(b, 32, k, false);
                    LanePut(made, 32, k, op switch
                    {
                        0xDB => _e.Binary(Opcode.And, x, y),
                        0xEB => _e.Binary(Opcode.Or, x, y),
                        0xEF => _e.Binary(Opcode.Xor, x, y),
                        _ => _e.Binary(Opcode.And, _e.Unary(Opcode.Not, x), y),
                    });
                }
                return;
            case 0xF6:
                // PSADBW: each eight bytes' absolute differences summed into
                // the low word of their quadword, the rest of it zero.
                for (int q = 0; q < bytes / 8; q++)
                {
                    VReg sum = LaneConst(0);
                    for (int k = 0; k < 8; k++)
                    {
                        VReg d = _e.Binary(Opcode.Sub, LaneAt(a, 8, 8 * q + k, false), LaneAt(b, 8, 8 * q + k, false));
                        sum = _e.Binary(Opcode.Add, sum, LaneChoose(LaneCompare(Opcode.LtS, d, LaneConst(0)), _e.Unary(Opcode.Neg, d), d));
                    }
                    LanePut(made, 32, 2 * q, sum);
                    LanePut(made, 32, 2 * q + 1, LaneConst(0));
                }
                return;
            case 0xF5:
                // PMADDWD: each pair of signed words multiplied and added.
                for (int k = 0; k < bytes / 4; k++)
                {
                    VReg low = _e.Binary(Opcode.Mul, LaneAt(a, 16, 2 * k, true), LaneAt(b, 16, 2 * k, true));
                    VReg high = _e.Binary(Opcode.Mul, LaneAt(a, 16, 2 * k + 1, true), LaneAt(b, 16, 2 * k + 1, true));
                    LanePut(made, 32, k, _e.Binary(Opcode.Add, low, high));
                }
                return;
            case 0x67: case 0x63: case 0x6B:
            {
                // A pack: a's lanes narrowed with saturation into the low
                // half of the width, b's into the high, all read first.
                int from = op == 0x6B ? 32 : 16, to = from / 2, lanes = bytes * 8 / from;
                (long least, long most) = op switch { 0x67 => (0L, 255L), 0x63 => (-128L, 127L), _ => (-32768L, 32767L) };
                List<VReg> got = new();
                for (int k = 0; k < lanes; k++) got.Add(LaneClamp(LaneAt(a, from, k, true), least, most));
                for (int k = 0; k < lanes; k++) got.Add(LaneClamp(LaneAt(b, from, k, true), least, most));
                for (int k = 0; k < got.Count; k++) LanePut(made, to, k, got[k]);
                return;
            }
            case 0x60: case 0x61: case 0x62: case 0x68: case 0x69: case 0x6A:
            {
                // An unpack: the low (or high) halves' lanes, a's and b's in turn.
                int bits = op is 0x60 or 0x68 ? 8 : op is 0x61 or 0x69 ? 16 : 32;
                int half = bytes * 4 / bits, first = op >= 0x68 ? half : 0;
                List<VReg> got = new();
                for (int k = 0; k < half; k++)
                {
                    got.Add(LaneAt(a, bits, first + k, false));
                    got.Add(LaneAt(b, bits, first + k, false));
                }
                for (int k = 0; k < got.Count; k++) LanePut(made, bits, k, got[k]);
                return;
            }
        }
        int width = op switch
        {
            0xFC or 0xF8 or 0xDC or 0xEC or 0xD8 or 0xE8 or 0x74 or 0x64 or 0xE0 or 0xDA or 0xDE => 8,
            0xFE or 0xFA or 0x76 or 0x66 => 32,
            _ => 16,
        };
        bool signed = op is 0xEC or 0xED or 0xE8 or 0xE9 or 0xD5 or 0xE5 or 0x64 or 0x65 or 0x66 or 0xEA or 0xEE;
        long top = (1L << width) - 1;
        for (int k = 0; k < bytes * 8 / width; k++)
        {
            VReg x = LaneAt(a, width, k, signed), y = LaneAt(b, width, k, signed);
            VReg v = op switch
            {
                0xFC or 0xFD or 0xFE => _e.Binary(Opcode.Add, x, y),
                0xF8 or 0xF9 or 0xFA => _e.Binary(Opcode.Sub, x, y),
                0xDC or 0xDD => LaneClamp(_e.Binary(Opcode.Add, x, y), 0, top),
                0xEC => LaneClamp(_e.Binary(Opcode.Add, x, y), -128, 127),
                0xED => LaneClamp(_e.Binary(Opcode.Add, x, y), -32768, 32767),
                0xD8 or 0xD9 => LaneClamp(_e.Binary(Opcode.Sub, x, y), 0, top),
                0xE8 => LaneClamp(_e.Binary(Opcode.Sub, x, y), -128, 127),
                0xE9 => LaneClamp(_e.Binary(Opcode.Sub, x, y), -32768, 32767),
                0xD5 => _e.Binary(Opcode.Mul, x, y),
                0xE5 => _e.Binary(Opcode.ShrS, _e.Binary(Opcode.Mul, x, y), 16),
                0xE4 => _e.Binary(Opcode.ShrU, _e.Binary(Opcode.Mul, x, y), 16),
                0x74 or 0x75 or 0x76 => _e.Unary(Opcode.Neg, LaneCompare(Opcode.Eq, x, y)),
                0x64 or 0x65 or 0x66 => _e.Unary(Opcode.Neg, LaneCompare(Opcode.GtS, x, y)),
                0xE0 or 0xE3 => _e.Binary(Opcode.ShrU, _e.Binary(Opcode.Add, _e.Binary(Opcode.Add, x, y), 1), 1),
                0xDA => LaneChoose(LaneCompare(Opcode.LtU, x, y), x, y),
                0xDE => LaneChoose(LaneCompare(Opcode.GtU, x, y), x, y),
                0xEA => LaneChoose(LaneCompare(Opcode.LtS, x, y), x, y),
                _ => LaneChoose(LaneCompare(Opcode.GtS, x, y), x, y),
            };
            LanePut(made, width, k, v);
        }
    }

    /// <summary>
    /// A shift of every lane of `bytes` by `count`, as PSLLW/D/Q, PSRLW/D/Q
    /// and PSRAW/D: `group` 0x71 words, 0x72 doublewords, 0x73 quadwords;
    /// `sub` 6 left, 2 right, 4 right arithmetic. A count past a lane's
    /// width empties it, or fills it with its sign.
    /// </summary>
    private void LanesShift(byte group, byte sub, int count, VReg made, VReg a, int bytes)
    {
        int bits = group == 0x71 ? 16 : group == 0x72 ? 32 : 64;
        IrType type = bits == 64 ? IrType.I64 : IrType.I32;
        for (int k = 0; k < bytes * 8 / bits; k++)
        {
            VReg v;
            if (sub == 4) v = _e.Binary(Opcode.ShrS, LaneAt(a, bits, k, true), Math.Min(count, bits - 1));
            else if (count >= bits) v = _e.Const(0, type);
            else v = _e.Binary(sub == 6 ? Opcode.Shl : Opcode.ShrU, LaneAt(a, bits, k, false), count);
            LanePut(made, bits, k, v);
        }
    }

    /// <summary>PSLLDQ (`sub` 7) and PSRLDQ (3): sixteen bytes moved by `count`, zeros in.</summary>
    private void LanesByteShift(byte sub, int count, VReg made, VReg a)
    {
        List<VReg> got = new();
        for (int k = 0; k < 16; k++)
        {
            int from = sub == 7 ? k - count : k + count;
            got.Add(from >= 0 && from < 16 ? LaneAt(a, 8, from, false) : LaneConst(0));
        }
        for (int k = 0; k < 16; k++) LanePut(made, 8, k, got[k]);
    }

    /// <summary>PSHUFW: each of four words from the one its two bits of `order` name, lowest first.</summary>
    private void LanesShuffle16(int order, VReg made, VReg a)
    {
        List<VReg> got = new();
        for (int k = 0; k < 4; k++) got.Add(LaneAt(a, 16, (order >> (2 * k)) & 3, false));
        for (int k = 0; k < 4; k++) LanePut(made, 16, k, got[k]);
    }

    /// <summary>PSHUFD (`prefix` 66: doublewords), PSHUFLW (F2: the low four words) and PSHUFHW (F3: the high four) over sixteen bytes.</summary>
    private void LanesShuffleWide(int prefix, int order, VReg made, VReg a)
    {
        if (prefix == 0x66)
        {
            List<VReg> dwords = new();
            for (int k = 0; k < 4; k++) dwords.Add(LaneAt(a, 32, (order >> (2 * k)) & 3, false));
            for (int k = 0; k < 4; k++) LanePut(made, 32, k, dwords[k]);
            return;
        }
        int at = prefix == 0xF2 ? 0 : 4;
        List<VReg> words = new();
        for (int k = 0; k < 8; k++) words.Add(k >= at && k < at + 4 ? LaneAt(a, 16, at + ((order >> (2 * (k - at))) & 3), false) : LaneAt(a, 16, k, false));
        for (int k = 0; k < 8; k++) LanePut(made, 16, k, words[k]);
    }

    /// <summary>SHUFPS (0xC6: the low two of a's floats by `order`, the high two of b's), UNPCKLPS (0x14) and UNPCKHPS (0x15), as their bits.</summary>
    private void LanesShuffleF32(byte op, int order, VReg made, VReg a, VReg b)
    {
        (VReg From, int Lane)[] picks = op switch
        {
            0xC6 => [(a, order & 3), (a, (order >> 2) & 3), (b, (order >> 4) & 3), (b, (order >> 6) & 3)],
            0x14 => [(a, 0), (b, 0), (a, 1), (b, 1)],
            _ => [(a, 2), (b, 2), (a, 3), (b, 3)],
        };
        List<VReg> got = picks.Select(p => LaneAt(p.From, 32, p.Lane, false)).ToList();
        for (int k = 0; k < 4; k++) LanePut(made, 32, k, got[k]);
    }

    /// <summary>
    /// A float operation over `bytes` of floats, the x87's (IEEE's single
    /// precision, as Packed's scalar form always was): a compare's lanes all
    /// ones or all zeros; min and max b's where either is a NaN, as MINPS
    /// and MAXPS; a conversion to an int truncated, 0x80000000 where it
    /// does not fit, as CVTTPS2DQ.
    /// </summary>
    private void LanesFloat(string name, VReg made, VReg a, VReg b, int bytes)
    {
        for (int k = 0; k < bytes / 4; k++)
        {
            VReg x = _e.Load(IrType.F32, a, 4 * k), y = name is "SqrtF32" or "IntToFloat32" or "FloatToInt32" ? x : _e.Load(IrType.F32, b, 4 * k);
            VReg v = name switch
            {
                "AddF32" => _e.Binary(Opcode.FAdd, x, y),
                "SubF32" => _e.Binary(Opcode.FSub, x, y),
                "MulF32" => _e.Binary(Opcode.FMul, x, y),
                "DivF32" => _e.Binary(Opcode.FDiv, x, y),
                "SqrtF32" => _e.Unary(Opcode.FSqrt, x),
                "MinF32" => LaneChoose(LaneCompare(Opcode.FLt, x, y), LaneAt(a, 32, k, false), LaneAt(b, 32, k, false)),
                "MaxF32" => LaneChoose(LaneCompare(Opcode.FGt, x, y), LaneAt(a, 32, k, false), LaneAt(b, 32, k, false)),
                "EqualF32" => _e.Unary(Opcode.Neg, LaneCompare(Opcode.FEq, x, y)),
                "LessF32" => _e.Unary(Opcode.Neg, LaneCompare(Opcode.FLt, x, y)),
                "LessEqualF32" => _e.Unary(Opcode.Neg, LaneCompare(Opcode.FLe, x, y)),
                "IntToFloat32" => _e.Unary(Opcode.IToF, R(LaneAt(a, 32, k, true)), IrType.F32),
                _ => FloatToIntTruncated(x),
            };
            _e.Store(RegOperand.Of(made), RegOperand.Of(v), 4 * k, 4);
        }
    }

    /// <summary>CVTTPS2DQ's lane: toward zero, and 0x80000000 -- the "integer indefinite" -- out of range or a NaN.</summary>
    private VReg FloatToIntTruncated(VReg x)
    {
        VReg wide = _e.Unary(Opcode.FConv, R(x), IrType.F64);
        VReg low = _e.Unary(Opcode.Bits, R(_e.Const(BitConverter.DoubleToInt64Bits(-2147483649.0), IrType.I64)), IrType.F64);
        VReg high = _e.Unary(Opcode.Bits, R(_e.Const(BitConverter.DoubleToInt64Bits(2147483648.0), IrType.I64)), IrType.F64);
        VReg inside = _e.Binary(Opcode.And, LaneCompare(Opcode.FGt, wide, low), LaneCompare(Opcode.FLt, wide, high));
        // Out of range, the truncation is left alone: what x87's FIST gives
        // there is its indefinite too, and the select makes it so either way.
        VReg truncated = _e.Unary(Opcode.FToI, R(wide), IrType.I32);
        return LaneChoose(inside, truncated, LaneConst(unchecked((int)0x80000000)));
    }
}
