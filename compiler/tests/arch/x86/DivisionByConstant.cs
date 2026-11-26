#nullable enable
using System.Runtime.InteropServices;
using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.X86;
using Block = Corsac.Lang.Ir.Block;

namespace Corsac.Tests.X86;

/// <summary>
/// DIVISION BY A CONSTANT AT THE SELECTOR, every path of it run: each
/// function here is one division or remainder of its parameter by a
/// constant, the constant an immediate in the IR as the selector is handed
/// it -- powers of two among them, which the optimiser's passes make shifts
/// before a compiled program's selector sees them (Peephole,
/// SignedPowerOfTwo), and remainders with no quotient beside them, which
/// DivRemReuse would make a multiply and a subtract. Every divisor form of
/// Select.DivideByConstant and DivisionMagic: a magic of the width, the Add
/// form (7, 19), an even divisor divided by its power of two first (14,
/// 28), unsigned divisors at and above 2^31 and 2^63, the most negative,
/// dividends known narrow (a byte, a widened word), and i386's 64-bit pair
/// division (UnsignedByConstant). Checked against C#'s own division over
/// edge dividends and pseudo-random ones:
///
/// - i386 (AddI386): a checker function a case, called by the end-to-end
///   executable's _start, which answers 1 where every input gave what C#
///   gives.
/// - long mode (LongMode): the module built by X64Backend, its code copied
///   into executable memory in this process and each function called with
///   every input -- the same System V convention this process's own calls
///   use. Only on an x86-64 Linux host; elsewhere it says it was not run.
/// </summary>
internal static class DivisionByConstant
{
    /// <summary>One function: its parameter (narrowed first where Narrow says: 8, a byte; 32, an I32 widened to I64) and the operation by the constant.</summary>
    private readonly record struct Case(string Name, IrType Type, Opcode Op, long Divisor, int Narrow)
    {
        public IrType ParamType => Narrow == 32 ? IrType.I32 : Type;
        public bool Signed => Op is Opcode.DivS or Opcode.RemS;
    }

    /// <summary>The same cases for both modes: i386 takes its I64 ones through its pair division.</summary>
    private static List<Case> Cases()
    {
        List<Case> cases = new();
        void Add(IrType type, bool signed, long divisor, int narrow = 0)
        {
            foreach (bool rem in new[] { false, true })
            {
                Opcode op = signed ? rem ? Opcode.RemS : Opcode.DivS : rem ? Opcode.RemU : Opcode.DivU;
                cases.Add(new Case($"divc{cases.Count}", type, op, divisor, narrow));
            }
        }
        foreach (long d in new long[] { 3, 7, 10, 14, 19, 28, 641, 1000000007, 0x7FFFFFFF, 0x80000001, 0xFFFFFFFE, 0xFFFFFFFF, 2, 8, 1024, 0x80000000 })
            Add(IrType.I32, false, unchecked((int)(uint)d));
        foreach (long d in new long[] { 3, 7, -7, 10, -10, 14, 641, 1000000007, -1000000007, 0x7FFFFFFF, -0x7FFFFFFF, 2, -2, 8, -8, 1024, int.MinValue })
            Add(IrType.I32, true, d);
        Add(IrType.I32, false, 7, narrow: 8);
        Add(IrType.I32, false, 300, narrow: 8);
        Add(IrType.I32, false, 14, narrow: 8);
        foreach (ulong d in new ulong[] { 3, 7, 14, 19, 641, 1000000007, 0x100000001, 1000000000000, 0x7FFFFFFFFFFFFFFF,
                     0x8000000000000001, 0xC000000000000000, 0xFFFFFFFFFFFFFFFE, 0xFFFFFFFFFFFFFFFF, 2, 8, 1UL << 40, 1UL << 63 })
            Add(IrType.I64, false, unchecked((long)d));
        // (i386 divides a signed 64-bit value by its magnitude's unsigned
        // division, UnsignedByConstant: the same pair code, both signs.)
        foreach (long d in new long[] { 3, 7, -7, 641, 0x100000001, -0x100000001, 1000000000000, 0x7FFFFFFFFFFFFFFF, -0x7FFFFFFFFFFFFFFF,
                     2, 8, -8, 1L << 40, -(1L << 40), long.MinValue })
            Add(IrType.I64, true, d);
        Add(IrType.I64, false, 7, narrow: 32);
        Add(IrType.I64, false, 28, narrow: 32);
        Add(IrType.I64, false, 0x100000001, narrow: 32);
        return cases;
    }

    /// <summary>Edge dividends and pseudo-random ones, as 64 bits; a 32-bit case takes the low half.</summary>
    private static ulong[] Inputs()
    {
        List<ulong> inputs = new()
        {
            0, 1, 2, 3, 6, 7, 8, 13, 14, 15, 27, 28, 255, 256, 299, 300, 640, 641, 642, 1023, 1024,
            0x7FFFFFFE, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFE, 0xFFFFFFFF, 0x100000000, 0x100000001,
            999999999999, 1000000000000, 0x7FFFFFFFFFFFFFFE, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0x8000000000000001,
            0xBFFFFFFFFFFFFFFF, 0xC000000000000000, 0xFFFFFFFFFFFFFFFE, 0xFFFFFFFFFFFFFFFF,
        };
        ulong seed = 0x9E3779B97F4A7C15UL;
        for (int k = 0; k < 40; k++)
        {
            seed = seed * 6364136223846793005UL + 1442695040888963407UL;
            inputs.Add(seed >> (int)(seed >> 58 & 63));
            inputs.Add(0UL - (seed >> (int)(seed >> 58 & 63)));
        }
        return inputs.ToArray();
    }

    /// <summary>What C# gives: the case's answer for an input, as the 64 bits its result register holds (a 32-bit one zero-extended).</summary>
    private static ulong Expected(Case c, ulong x)
    {
        if (c.Narrow == 8) x &= 0xFF;
        if (c.Narrow == 32) x &= 0xFFFFFFFF;
        if (c.Type == IrType.I32)
        {
            uint ux = (uint)x, ud = unchecked((uint)c.Divisor);
            int sx = unchecked((int)ux), sd = unchecked((int)c.Divisor);
            uint r = c.Op switch
            {
                Opcode.DivU => ux / ud,
                Opcode.RemU => ux % ud,
                Opcode.DivS => unchecked((uint)(sx / sd)),
                _ => unchecked((uint)(sx % sd)),
            };
            return r;
        }
        ulong d = unchecked((ulong)c.Divisor);
        long s = unchecked((long)x);
        return c.Op switch
        {
            Opcode.DivU => x / d,
            Opcode.RemU => x % d,
            Opcode.DivS => unchecked((ulong)(s / c.Divisor)),
            _ => unchecked((ulong)(s % c.Divisor)),
        };
    }

    private static void Build(Module m, Case c)
    {
        Function f = new(c.Name, c.Type);
        f.Params.Add(f.NewReg(c.ParamType, "n"));
        Builder b = new(f, f.NewBlock("entry"));
        m.Functions.Add(f);
        VReg n = f.Params[0];
        if (c.Narrow == 8) n = b.Unary(Opcode.ZExt8, n);
        if (c.Narrow == 32) n = b.Unary(Opcode.ZExt32, n);
        b.Ret(RegOperand.Of(b.Binary(c.Op, RegOperand.Of(n), new ImmOperand(c.Divisor, c.Type), c.Type)));
    }

    /// <summary>
    /// The cases into i386's end-to-end module, and a checker for each that
    /// answers 1 where every input gives C#'s answer: the checkers' names,
    /// for _start to call.
    /// </summary>
    public static List<string> AddI386(Module m)
    {
        List<string> checkers = new();
        ulong[] inputs = Inputs();
        foreach (Case c in Cases())
        {
            Build(m, c);
            Function check = new(c.Name + "_check", IrType.I32);
            Builder b = new(check, check.NewBlock("entry"));
            m.Functions.Add(check);
            VReg all = b.Const(1, IrType.I32);
            foreach (ulong x in inputs)
            {
                ImmOperand argument = c.ParamType == IrType.I32 ? new(unchecked((int)(uint)x), IrType.I32) : new(unchecked((long)x), IrType.I64);
                VReg got = b.Call(c.Name, c.Type, argument)!;
                ulong want = Expected(c, x);
                ImmOperand expected = c.Type == IrType.I32 ? new(unchecked((int)(uint)want), IrType.I32) : new(unchecked((long)want), IrType.I64);
                all = b.Binary(Opcode.And, RegOperand.Of(all), RegOperand.Of(b.Binary(Opcode.Eq, RegOperand.Of(got), expected, IrType.I32)), IrType.I32);
            }
            b.Ret(RegOperand.Of(all));
            checkers.Add(check.Name);
        }
        return checkers;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr address, nuint length, int protection, int flags, int fd, IntPtr offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr address, nuint length, int protection);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr address, nuint length);

    /// <summary>The cases through X64Backend, run in this process with every input; each failure reported through `check`.</summary>
    public static unsafe void LongMode(Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.WriteLine("[--] long mode's division by a constant not run: this host is no x86-64 Linux");
            return;
        }
        List<Case> cases = Cases();
        Module m = new("division-by-constant-long");
        foreach (Case c in cases) Build(m, c);
        Target target = Target.Current;
        bool wasLong = X86Mode.Long;
        ObjectFile obj;
        List<string> errors = new();
        try
        {
            Target.Current = Target.X86_64;
            obj = new Corsac.Lang.X64.X64Backend { Hosted = true }.Generate(m, errors);
        }
        finally
        {
            Target.Current = target;
            X86Mode.Long = wasLong;
        }
        check(errors.Count == 0, "long mode: the division cases compile" + (errors.Count > 0 ? ": " + errors[0] : ""));
        if (errors.Count > 0) return;
        Section text = obj.Section(".text");
        byte[] code = text.Content();
        Dictionary<string, Symbol> symbols = new(StringComparer.Ordinal);
        foreach (Symbol symbol in obj.Symbols)
            if (symbol.IsFunction && symbol.Section == text) symbols[symbol.Name] = symbol;
        // Each function is its code and nothing outside it: no relocation
        // lands in it (a constant pool's, a call's), so the bytes run where
        // they are copied.
        foreach (Relocation reloc in text.Relocs)
            foreach (Case c in cases)
                if (symbols.TryGetValue(c.Name, out Symbol? s) && reloc.Offset >= s.Offset && reloc.Offset < s.Offset + s.Size)
                    check(false, $"long mode: {c.Name} has a relocation to {reloc.Symbol}, and cannot run where it is copied");

        nuint length = (nuint)((code.Length + 4095) & ~4095);
        IntPtr memory = mmap(IntPtr.Zero, length, 3, 0x22, -1, IntPtr.Zero);   // read and write; private, anonymous
        check(memory != new IntPtr(-1), "long mode: executable memory for the division cases");
        if (memory == new IntPtr(-1)) return;
        try
        {
            Marshal.Copy(code, 0, memory, code.Length);
            bool executable = mprotect(memory, length, 5) == 0;   // read and execute
            check(executable, "long mode: the division cases' memory made executable");
            if (!executable) return;
            ulong[] inputs = Inputs();
            int wrong = 0, runs = 0;
            foreach (Case c in cases)
            {
                if (!symbols.TryGetValue(c.Name, out Symbol? s))
                {
                    check(false, $"long mode: {c.Name} has no symbol");
                    continue;
                }
                IntPtr entry = memory + (nint)s.Offset;
                foreach (ulong x in inputs)
                {
                    // A 32-bit argument with its upper half not the callee's
                    // to trust (System V leaves it undefined), and a 32-bit
                    // result read from the low half alone: the code may rely
                    // on neither being clean.
                    ulong argument = c.ParamType == IrType.I32 ? (x & 0xFFFFFFFF) | 0xA5A5A5A500000000UL : x;
                    ulong got = ((delegate* unmanaged<ulong, ulong>)entry)(argument);
                    if (c.Type == IrType.I32) got &= 0xFFFFFFFF;
                    runs++;
                    ulong want = Expected(c, x);
                    if (got != want && wrong++ < 20)
                        Console.WriteLine($"long mode: {c.Op} {(c.Type == IrType.I32 ? "I32" : "I64")} narrow {c.Narrow}: {x:X} by {c.Divisor} gave {got:X}, not {want:X}");
                }
            }
            check(wrong == 0, $"long mode: {cases.Count} divisions and remainders by a constant, {runs} runs, each C#'s answer ({wrong} wrong)");
        }
        finally
        {
            munmap(memory, length);
        }
    }
}
