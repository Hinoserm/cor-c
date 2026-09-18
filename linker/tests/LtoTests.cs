using System.Security.Cryptography;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

public static class LtoTests
{
    public static void Run()
    {
        ObjectFile Function(int value, bool global = true)
        {
            ObjectFile obj = new();
            Section text = new(".text", SectionKind.Code);
            text.Bytes.AddRange(new byte[] { 0xb8, (byte)value, 0, 0, 0, 0xc3 });
            obj.Sections.Add(text);
            obj.Symbols.Add(new Symbol { Name = "answer", Section = text, Size = 6, IsFunction = true, Global = global });
            OptimizationSummary summary = new();
            summary.Returns.Add(new ConstantReturn("answer", value, SHA256.HashData(text.Bytes.ToArray())));
            summary.Attach(obj);
            return obj;
        }
        ObjectFile Caller()
        {
            ObjectFile obj = new();
            Section text = new(".text", SectionKind.Code);
            text.Bytes.AddRange(new byte[] { 0xe8, 0, 0, 0, 0, 0x89, 0xc3, 0xb8, 1, 0, 0, 0, 0xcd, 0x80 });
            text.Relocs.Add(new Relocation(1, "answer", -4, RelocKind.Rel32));
            obj.Sections.Add(text);
            obj.Symbols.Add(new Symbol { Name = "_start", Section = text, Size = 14, IsFunction = true });
            obj.Symbols.Add(new Symbol { Name = "answer" });
            OptimizationSummary summary = new();
            summary.Calls.Add(new DirectCall(1, "answer"));
            summary.Attach(obj);
            return obj;
        }
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Reject(Action action)
        {
            try { action(); } catch (ElfFormatException) { return; } catch (LinkException) { return; }
            throw new Exception("Malformed optimization metadata was accepted");
        }

        ObjectFile caller = ElfReader.ReadObject(ElfWriter.WriteObject(Caller()));
        ObjectFile callee = ElfReader.ReadObject(ElfWriter.WriteObject(Function(42)));
        Require(OptimizationSummary.Read(callee)!.Returns.Single().Value == 42, "summary round trip");
        Require(LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }) == 1, "cross-object call not folded");
        Require(caller.Section(".text").Bytes[0] == 0xb8 && caller.Section(".text").Bytes[1] == 42, "replacement bytes");
        Require(caller.Section(".text").Relocs.Count == 0, "call relocation not removed");
        Require(caller.Section(".text").Bytes.Count == 14, "code size changed");
        Require(Linker.Link(new[] { ("caller", caller), ("callee", callee) }, "_start").Length > 0, "optimized image rejected");

        caller = Caller(); callee = Function(42);
        Require(LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }, false) == 0, "disabled LTO changed code");
        Require(caller.Section(".text").Bytes[0] == 0xe8, "disabled LTO patched a call");

        caller = Caller(); callee = Function(42);
        callee.Sections.RemoveAll(s => s.Name == OptimizationSummary.SectionName);
        Require(LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }) == 0, "missing summary inferred as pure");

        caller = Caller(); callee = Function(42);
        caller.Sections.RemoveAll(s => s.Name == OptimizationSummary.SectionName);
        Require(LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }) == 0, "uncertified relocation treated as call");

        caller = Caller(); callee = Function(42);
        caller.Symbols.Add(new Symbol { Name = "answer", Section = caller.Section(".text"), Offset = 5, Size = 2, IsFunction = true, Global = false });
        Require(LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }) == 0, "global summary overrode local definition");

        caller = Caller(); callee = Function(42);
        callee.Section(".text").Bytes[1] ^= 1;
        Reject(() => LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }));
        Require(caller.Section(".text").Bytes[0] == 0xe8, "validation failure mutated caller");

        caller = Caller(); callee = Function(42);
        callee.Section(OptimizationSummary.SectionName).Bytes[4] = 2;
        Reject(() => LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }));

        caller = Caller(); callee = Function(42);
        callee.Section(OptimizationSummary.SectionName).Bytes.RemoveAt(8);
        Reject(() => LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }));

        caller = Caller(); callee = Function(42);
        caller.Section(".text").Relocs[0] = new Relocation(1, "answer", -4, RelocKind.Abs32);
        Reject(() => LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) }));
        Reject(() => LinkTimeOptimizer.Run(new[] { ("a", Function(42)), ("b", Function(43)) }));

        caller = Caller(); callee = Function(42);
        LinkTimeOptimizer.Run(new[] { ("caller", caller), ("callee", callee) });
        Section bss = new(".bss", SectionKind.Uninitialised) { ZeroBytes = 4096, Align = 16 };
        callee.Sections.Add(bss);
        Linker.FlatImage flat = Linker.LinkFlat(new[] { ("caller", caller), ("callee", callee) }, "_start", 0x10000);
        Require(flat.Entry == 0x10000 && flat.Bytes[0] == 0xb8, "flat entry or optimization incorrect");
        Require(flat.BssSize >= 4096 && flat.MemorySize > flat.Bytes.Length, "flat BSS lost");
        Require(flat.MemorySize == 4128, "flat memory size omitted BSS alignment padding");
        byte[] kernel = Linker.Link(new[] { ("caller", caller), ("callee", callee) }, "_start", 0xc0100000, 0x100000);
        uint entry = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(kernel.AsSpan(24, 4));
        Require(entry >= 0x100000 && entry < 0x101000, "kernel physical entry not relocated");
        ObjectFile hosted = Caller(), bare = Function(42);
        new TargetContract(0).Attach(hosted);
        new TargetContract(1).Attach(bare);
        Reject(() => TargetContract.Validate(new[] { ("hosted", hosted), ("bare", bare) }));
        Reject(() => Linker.Link(new[] { ("hosted", hosted), ("bare", bare) }, "_start"));
        Reject(() => Linker.LinkFlat(new[] { ("hosted", hosted), ("bare", bare) }, "_start", 0x10000));
        ObjectFile bare2 = Function(43);
        new TargetContract(1).Attach(bare2);
        TargetContract.Validate(new[] { ("bare", bare), ("bare2", bare2) });
        bare2.Section(TargetContract.SectionName).Bytes[4] = 9;
        Reject(() => TargetContract.Validate(new[] { ("bare2", bare2) }));
        ObjectFile layoutA = Caller(), layoutB = Function(42);
        ManagedLayoutContract.Attach(layoutA, new[] { new ManagedTypeLayout("type:Example", new byte[32]) });
        ManagedLayoutContract.Attach(layoutB, new[] { new ManagedTypeLayout("type:Example", new byte[32]) });
        ManagedLayoutContract.Validate(new[] { ("a", layoutA), ("b", ElfReader.ReadObject(ElfWriter.WriteObject(layoutB))) });
        ObjectFile mismatch = Function(42);
        byte[] different = new byte[32]; different[0] = 1;
        ManagedLayoutContract.Attach(mismatch, new[] { new ManagedTypeLayout("type:Example", different) });
        Reject(() => LinkTimeOptimizer.Run(new[] { ("a", layoutA), ("mismatch", mismatch) }));
        Require(layoutA.Section(".text").Bytes[0] == 0xe8, "layout failure mutated LTO input");
        Reject(() => Linker.Link(new[] { ("a", layoutA), ("mismatch", mismatch) }, "_start"));
        Reject(() => Linker.LinkFlat(new[] { ("a", layoutA), ("mismatch", mismatch) }, "_start", 0x10000));
        layoutB.Section(ManagedLayoutContract.SectionName).Bytes[4] = 99;
        Reject(() => ManagedLayoutContract.Validate(new[] { ("b", layoutB) }));
        ObjectFile missingLayout = Caller();
        new TargetContract(0, requiresManagedLayouts: true).Attach(missingLayout);
        Reject(() => TargetContract.Validate(new[] { ("missing", missingLayout) }));
        ObjectFile cpuA = Function(1), cpuB = Function(2);
        var profile = new X86CodeGenerationContract("k6-3+", "k6-3+", "x87", true, true, true);
        profile.Attach(cpuA); profile.Attach(cpuB);
        Require(X86CodeGenerationContract.Read(ElfReader.ReadObject(ElfWriter.WriteObject(cpuA))) == profile,
            "CPU permissions did not survive object serialization");
        X86CodeGenerationContract.ValidateRegeneration(cpuA, cpuB);
        X86CodeGenerationContract.ValidateTarget(new[] { ("plus", cpuA) }, profile);
        Reject(() => X86CodeGenerationContract.ValidateTarget(new[] { ("plus", cpuA) }, Corsac.Lang.X86.X86Cpu.Parse(["--cpu=486"]).Contract));
        Reject(() => X86CodeGenerationContract.ValidateTarget(new[] { ("plus", cpuA) }, Corsac.Lang.X86.X86Cpu.Parse(["--cpu=k6-3+", "--disable-mmx"]).Contract));
        ObjectFile excluded = Function(3);
        new X86CodeGenerationContract("k6-3+", "486", "x87", false, false, false).Attach(excluded);
        Reject(() => X86CodeGenerationContract.ValidateRegeneration(cpuA, excluded));
        Reject(() => new X86CodeGenerationContract("486", "486", "none", true, false, false).Attach(Function(4)));
        cpuB.Section(X86CodeGenerationContract.SectionName).Bytes[4] = (byte)'9';
        Reject(() => TargetContract.Validate(new[] { ("invalid-cpu", cpuB) }));
        Console.WriteLine("  LTO metadata, scope, rejection, static/flat/physical-link checks passed");
    }
}
