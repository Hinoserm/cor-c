using System.Diagnostics;
using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Elf;
using Corsac.Lang.X86;

namespace Corsac.Tests.X86;

public static class PackedMemoryBenchmarks
{
    private static ImmOperand I(int value) => new(value, IrType.I32);
    private static RegOperand R(VReg value) => new(value);

    public static int Run()
    {
        const int iterations = 3000000;
        Target.Current = Target.X86;
        string work = Path.Combine(Path.GetTempPath(), "corc-packed-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Console.WriteLine("Native Linux packed-memory benchmark; median of five runs after warmup; " + iterations + " operations/run");
        Console.WriteLine("operation,bytes,mmx,text_bytes,median_ms,ns_per_operation");
        List<string> rows = ["operation,bytes,mmx,text_bytes,median_ms,ns_per_operation"];
        foreach (string operation in new[] { "copy", "zero" })
        foreach (int length in new[] { 32, 64, 128 })
        foreach (bool packed in new[] { false, true })
        {
            Target.X86.X86Profile = X86Cpu.Parse(packed ? ["--cpu=pentium-mmx"] : ["--cpu=pentium-mmx", "--disable-mmx"]);
            Module module = new("packed-benchmark") { Entry = "_start" };
            Function function = new("_start", IrType.Void) { Exported = true }; module.Functions.Add(function);
            Builder b = new(function, function.NewBlock("entry"));
            FrameSlot source = function.NewSlot(length, 8, "source"), destination = function.NewSlot(length, 8, "destination");
            for (int offset = 0; offset < length; offset += 4) b.Store(new SlotOperand(source), I(0x12345678), offset, 4);
            VReg count = b.Const(iterations, IrType.I32);
            Block loop = function.NewBlock("loop"), done = function.NewBlock("done"); b.Jump(loop); b.SetBlock(loop);
            b.Emit(operation == "copy" ? Opcode.MemCopy : Opcode.MemSet, null,
                new SlotOperand(destination), operation == "copy" ? new SlotOperand(source) : I(0), I(length));
            b.CopyTo(count, R(b.Binary(Opcode.Sub, count, 1)));
            b.Branch(b.Binary(Opcode.Ne, count, 0), loop, done); b.SetBlock(done);
            VReg failed = b.Binary(Opcode.Ne, b.Load(IrType.I32, new SlotOperand(destination), length - 4, 4, false), operation == "copy" ? 0x12345678 : 0);
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
            string row = FormattableString.Invariant($"{operation},{length},{packed},{obj.Section(".text").Bytes.Count},{median:F3},{median * 1000000 / iterations:F3}");
            rows.Add(row); Console.WriteLine(row);
        }
        File.WriteAllLines(Path.Combine(work, "results.csv"), rows);
        Console.WriteLine("Evidence: " + work); return 0;
    }
}
