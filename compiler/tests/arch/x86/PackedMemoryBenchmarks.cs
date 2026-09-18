using System.Diagnostics;
using Target = Corsac.Lang.Target;
using Corsac.Lang.Ir;
using Corsac.Lang.Elf;
using Corsac.Lang.X86;

namespace Corsac.Tests.X86;

public static class PackedMemoryBenchmarks
{
    private static ImmOperand I(int value) => new(value, IrType.I32);
    private static RegOperand R(VReg value) => new(value);

    public static int Run(string? filter = null)
    {
        Target.Current = Target.X86;
        string work = Path.Combine(Path.GetTempPath(), "corc-packed-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Console.WriteLine("Native Linux packed benchmark; median of five runs after warmup; process startup included");
        Console.WriteLine("operation,bytes,mmx,iterations,text_bytes,median_ms,ns_per_operation");
        List<string> rows = ["operation,bytes,mmx,iterations,text_bytes,median_ms,ns_per_operation"];
        foreach (string operation in new[] { "copy", "zero", "add8", "add16", "add32", "sub8", "sub16", "sub32", "and32", "or32", "xor32", "mul16", "mulhigh16",
            "shl16", "shl32", "shr16", "shr32", "sar16", "sar32" }.Where(operation => filter is null || operation.StartsWith(filter, StringComparison.Ordinal)))
        foreach (int length in new[] { 32, 64, 128 })
        foreach (bool packed in new[] { false, true })
        {
            int iterations = operation is "copy" or "zero" ? 30000000 : 3000000;
            Target.X86.X86Profile = X86Cpu.Parse(packed ? ["--cpu=pentium-mmx"] : ["--cpu=pentium-mmx", "--disable-mmx"]);
            Module module = new("packed-benchmark") { Entry = "_start" };
            Function function = new("_start", IrType.Void) { Exported = true }; module.Functions.Add(function);
            Builder b = new(function, function.NewBlock("entry"));
            FrameSlot source = function.NewSlot(length, 8, "source"), right = function.NewSlot(length, 8, "right"), destination = function.NewSlot(length, 8, "destination");
            for (int offset = 0; offset < length; offset += 4)
            { b.Store(new SlotOperand(source), I(0x12345678), offset, 4); b.Store(new SlotOperand(right), I(0x01010101), offset, 4); }
            VReg count = b.Const(iterations, IrType.I32);
            Block loop = function.NewBlock("loop"), done = function.NewBlock("done"); b.Jump(loop); b.SetBlock(loop);
            int expected = operation == "copy" ? 0x12345678 : 0;
            if (operation is "copy" or "zero")
                b.Emit(operation == "copy" ? Opcode.MemCopy : Opcode.MemSet, null,
                    new SlotOperand(destination), operation == "copy" ? new SlotOperand(source) : I(0), I(length));
            else
            {
                int width = operation.EndsWith("8") ? 1 : operation.EndsWith("16") ? 2 : 4;
                Opcode op = operation[..3] switch { "add" => Opcode.Add, "sub" => Opcode.Sub, "and" => Opcode.And,
                    "xor" => Opcode.Xor, "mul" => Opcode.Mul, "shl" => Opcode.Shl, "shr" => Opcode.ShrU, "sar" => Opcode.ShrS, _ => Opcode.Or };
                bool shift = op is Opcode.Shl or Opcode.ShrS or Opcode.ShrU;
                for (int offset = 0; offset < length; offset += width)
                {
                    VReg left = b.Load(IrType.I32, new SlotOperand(source), offset, width, operation == "mulhigh16" || op == Opcode.ShrS);
                    VReg value = shift ? b.Binary(op, left, 5) : b.Binary(op, left, b.Load(IrType.I32, new SlotOperand(right), offset, width, operation == "mulhigh16"));
                    if (operation == "mulhigh16") value = b.Binary(Opcode.ShrS, value, 16);
                    b.Store(new SlotOperand(destination), R(value), offset, width);
                }
                expected = op switch { Opcode.Add => 0x13355779, Opcode.Sub => 0x11335577, Opcode.And => 0x00000000,
                    Opcode.Or => 0x13355779, Opcode.Xor => 0x13355779, _ => unchecked((int)0x4634ce78) };
                if (operation == "mulhigh16") expected = 0x00120056;
                if (shift) expected = op == Opcode.Shl ? (width == 2 ? 0x4680cf00 : 0x468acf00)
                    : width == 2 ? 0x009102b3 : 0x0091a2b3;
            }
            b.CopyTo(count, R(b.Binary(Opcode.Sub, count, 1)));
            b.Branch(b.Binary(Opcode.Ne, count, 0), loop, done); b.SetBlock(done);
            VReg failed = b.Binary(Opcode.Ne, b.Load(IrType.I32, new SlotOperand(destination), length - 4, 4, false), expected);
            b.Syscall(I(1), [R(failed)]); b.Unreachable();
            X86Backend backend = new(); List<string> errors = new(); ObjectFile obj = backend.Generate(module, errors);
            if (errors.Count != 0) throw new Exception(string.Join("; ", errors));
            string stem = operation + "-" + length + "-" + packed;
            File.WriteAllText(Path.Combine(work, stem + ".asm"), backend.Assembly(module));
            string path = Path.Combine(work, stem); File.WriteAllBytes(path, Linker.Link([obj], "_start"));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            List<double> timings = [];
            for (int run = 0; run < 6; run++)
            {
                Stopwatch watch = Stopwatch.StartNew(); using Process process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
                if (!process.WaitForExit(30000)) { process.Kill(true); process.WaitForExit(); throw new Exception("Benchmark timed out: " + path); }
                watch.Stop(); if (process.ExitCode != 0) throw new Exception("Benchmark correctness failed: " + path + " exit=" + process.ExitCode);
                if (run != 0) timings.Add(watch.Elapsed.TotalMilliseconds);
            }
            timings.Sort(); double median = timings[2];
            string row = FormattableString.Invariant($"{operation},{length},{packed},{iterations},{obj.Section(".text").Bytes.Count},{median:F3},{median * 1000000 / iterations:F3}");
            rows.Add(row); Console.WriteLine(row);
        }
        File.WriteAllLines(Path.Combine(work, "results.csv"), rows);
        Console.WriteLine("Evidence: " + work); return 0;
    }
}
