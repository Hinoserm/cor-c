using System.Security.Cryptography;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

public static class CoalescingTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action)
        {
            try { action(); } catch (ElfFormatException) { return; } catch (LinkException) { return; }
            throw new Exception("Invalid coalescing contract accepted");
        }
        ObjectFile Definition(byte code, byte semantic = 1, bool certify = true)
        {
            ObjectFile obj = new();
            Section text = new(".text", SectionKind.Code);
            text.Bytes.AddRange(new byte[] { code, 0xc3 }); obj.Sections.Add(text);
            obj.Symbols.Add(new Symbol { Name = "specialization", Section = text, Size = 2, IsFunction = true });
            if (certify) CoalescingContract.Attach(obj, new Dictionary<string, byte[]> { ["specialization"] = SHA256.HashData(new[] { semantic }) });
            return obj;
        }
        ObjectFile a = Definition(0x90), b = Definition(0x91);
        b = ElfReader.ReadObject(ElfWriter.WriteObject(b));
        Check(CoalescingContract.Read(b).Count == 1, "contract round trip");
        Section maps = new(".corsac.stackmaps", SectionKind.Note);
        maps.Bytes.AddRange(new byte[4]); maps.Relocs.Add(new Relocation(0, "specialization", 0, RelocKind.Abs32)); b.Sections.Add(maps);
        Check(DefinitionCoalescer.Run(new[] { ("b", b), ("a", a) }, true) == 1, "validation plan");
        Check(b.Symbols.Single(symbol => symbol.Name == "specialization").IsDefined, "validation mutated input");
        Check(DefinitionCoalescer.Run(new[] { ("b", b), ("a", a) }) == 1, "equivalent different machine code rejected");
        Check(a.Symbols.Single(symbol => symbol.Name == "specialization").IsDefined, "input-name ownership not deterministic");
        Check(!b.Symbols.Single(symbol => symbol.Name == "specialization").IsDefined, "loser remains global");
        Check(maps.Relocs[0].Symbol == "__corsac_retained_specialization", "stack maps point to wrong body");
        Check(b.Section(".text").Bytes[0] == 0x91, "retained code changed");
        Check(DefinitionCoalescer.Run(new[] { ("a", a), ("b", b) }) == 0, "final-link revalidation failed");
        a = Definition(0x90); b = Definition(0x90, 2);
        Reject(() => DefinitionCoalescer.Run(new[] { ("a", a), ("b", b) }));
        Check(a.Symbols.Count == 1 && b.Symbols.Count == 1, "conflict mutated definitions");
        Reject(() => DefinitionCoalescer.Run(new[] { ("a", Definition(0x90)), ("b", Definition(0x90, certify: false)) }));
        Reject(() => DefinitionCoalescer.Run(new[] { ("same", Definition(0x90)), ("same", Definition(0x90)) }));
        b = Definition(0x90); b.Section(".text").Bytes[0] = 0x92;
        Reject(() => CoalescingContract.Read(b));
        b = Definition(0x90); b.Section(CoalescingContract.SectionName).Bytes[4] = 2;
        Reject(() => CoalescingContract.Read(b));
        b = Definition(0x90); b.Section(CoalescingContract.SectionName).Bytes.RemoveAt(12);
        Reject(() => CoalescingContract.Read(b));
        a = Definition(0x90); b = Definition(0x90);
        b.Symbols.Add(new Symbol { Name = "__corsac_retained_specialization", Global = false });
        Reject(() => DefinitionCoalescer.Run(new[] { ("a", a), ("b", b) }));
        Check(a.Symbols.Count == 1 && b.Symbols.Count == 2, "alias collision mutated input");
        ObjectFile local = new();
        Section data = new(".rodata", SectionKind.ReadOnlyData); data.Bytes.AddRange(new byte[] { 0, 0, 0, 0, 42 });
        data.Relocs.Add(new Relocation(0, "private", 0, RelocKind.Abs32)); local.Sections.Add(data);
        Symbol root = new() { Name = "descriptor", Section = data, Size = 4 }; local.Symbols.Add(root);
        local.Symbols.Add(new Symbol { Name = "private", Section = data, Offset = 4, Size = 1, Global = false });
        CoalescingContract.Attach(local, new Dictionary<string, byte[]> { [root.Name] = new byte[32] });
        data.Bytes[4] = 43;
        Reject(() => CoalescingContract.Read(local));
        // A malformed LTO record must fail before coalescing changes symbols.
        a = Definition(0x90); b = Definition(0x90);
        OptimizationSummary summary = new(); summary.Returns.Add(new ConstantReturn("missing", 1, new byte[32])); summary.Attach(b);
        Reject(() => LinkTimeOptimizer.Run(new[] { ("a", a), ("b", b) }));
        Check(b.Symbols.Count == 1 && b.Symbols[0].IsDefined, "LTO validation changed symbol ownership");
        Console.WriteLine("  coalescing ownership, integrity, conflict and metadata checks passed");
    }
}
