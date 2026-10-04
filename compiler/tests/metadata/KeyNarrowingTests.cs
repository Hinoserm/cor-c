using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;

namespace Corsac.Tests.Metadata;

/// <summary>
/// The key questions narrowed to the key's static type: a table keyed by a
/// tuple of a class asks that class's Equals and GetHashCode through its own
/// descriptor -- `__virtual:t_Key+...` in the unit's hints, never object's --
/// and a record's members are compared as their own types, not through the
/// comparer's one shared copy.
/// </summary>
public static class KeyNarrowingTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "KeyNarrowing.cor");
        File.WriteAllText(source, "using System.Collections.Generic; "
            + "class Key { public int Id; public override bool Equals(object? o) => o is Key k && k.Id == Id; public override int GetHashCode() => Id; } "
            + "record Rec(Key K, int N); "
            + "class Program { public static int Main() { var d = new Dictionary<(Key, int), int>(); d[(new Key(), 1)] = 2; "
            + "return d.Count + (new Rec(new Key(), 1) == new Rec(new Key(), 1) ? 1 : 0); } }");
        string[] runtime = RuntimeDeclarations.Sources();
        var front = Frontend.Compile(new[] { source }.Concat(runtime).ToArray(), "key-narrowing", false,
            libraryPaths: runtime, elsewherePaths: runtime) ?? throw new Exception("Key narrowing binding failed");
        List<CompileError> errors = new();
        Module module = Lowering.Lower(front.Bound, front.Unit, "key-narrowing", false, errors, new());
        if (errors.Count != 0) throw new Exception(string.Join("; ", errors));

        IEnumerable<Instr> Instrs(Function f) => f.Blocks.SelectMany(b => b.Instrs);
        Function Named(string name) => module.Functions.SingleOrDefault(f => f.Name == name)
            ?? throw new Exception("No " + name + " was made for a tuple keyed by a class");
        foreach (string name in new[] { "__key_equals$Key", "__key_hash$Key" })
        {
            Instr[] asked = Instrs(Named(name)).Where(i => i.Op == Opcode.CallIndirect).ToArray();
            if (asked.Length != 1 || asked[0].DispatchType != "t_Key")
                throw new Exception(name + " does not ask through Key's descriptor: " + string.Join(", ", asked.Select(i => i.DispatchType)));
        }
        Function[] tupleQuestions = module.Functions.Where(f => f.Name.StartsWith("__struct_equals$", StringComparison.Ordinal)
                                                             || f.Name.StartsWith("__struct_hash$", StringComparison.Ordinal)).ToArray();
        if (tupleQuestions.Length == 0)
            throw new Exception("The tuple key was not compared by its items");
        foreach (Function f in tupleQuestions)
            if (Instrs(f).Any(i => i.Op == Opcode.Call && i.Callee is "__key_equals" or "__key_hash"))
                throw new Exception(f.Name + " asks a class-typed item through object's shared routine");

        Function[] record = module.Functions.Where(f => f.Display is string d
            && (d.StartsWith("Rec.Equals(", StringComparison.Ordinal) || d.StartsWith("Rec.GetHashCode(", StringComparison.Ordinal))).ToArray();
        if (!record.Any(f => Instrs(f).Any(i => i.Op == Opcode.Call && i.Callee == "__key_equals$Key"))
            || !record.Any(f => Instrs(f).Any(i => i.Op == Opcode.Call && i.Callee == "__key_hash$Key")))
            throw new Exception("A record's member of a class type is not compared as that class");
        if (record.Any(f => Instrs(f).Any(i => i.Op == Opcode.Call && (i.Callee is "__key_equals" or "__key_hash"
                                                                       || i.Callee?.Contains("EqualityComparer", StringComparison.Ordinal) == true))))
            throw new Exception("A record's members are still compared through the shared comparer");
        Console.WriteLine("key narrowing: class-keyed tuple and record members asked through the class's own descriptor");
    }
}
