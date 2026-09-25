using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

/// A type nested in a generic one, imported through the index: its own
/// declaration slice names none of the outer's parameters, so it must come
/// from the whole file, where the parser gives it them (List<T>.Enumerator).
public static class NestedGenericImportTests
{
    public static void Run(string work)
    {
        string library = Path.Combine(work, "NestedGeneric.cor");
        string caller = Path.Combine(work, "NestedGenericCaller.cor");
        string index = Path.Combine(work, "nested-generic.idx");
        File.WriteAllText(library, "public class Bag<T> { T[] items = new T[4]; int count; "
            + "public void Add(T item) { items[count] = item; count = count + 1; } "
            + "public T At(int i) => items[i]; public int Count => count; "
            + "public Walker GetWalker() { return new Walker(this); } "
            + "public struct Walker { private readonly Bag<T> bag; private int at; "
            + "internal Walker(Bag<T> bag) { this.bag = bag; at = -1; } "
            + "public T Current => bag.At(at); public bool MoveNext() { at = at + 1; return at < bag.Count; } } }");
        SourceIndexBuilder.Write(index, new[] { library }, "NestedGeneric");
        File.WriteAllText(caller, "class Caller { public static int Run() { Bag<int> bag = new Bag<int>(); bag.Add(2); bag.Add(3); "
            + "int sum = 0; Bag<int>.Walker w = bag.GetWalker(); while (w.MoveNext()) sum = sum + w.Current; return sum; } }");
        using (IndexedDeclarations declarations = new(index, "NestedGeneric", new[] { caller }))
        {
            _ = Frontend.Compile(new[] { caller }, "nested-generic", true, declarations: declarations)
                ?? throw new Exception("A type nested in a generic one did not import with the outer's parameters");
        }
        Console.WriteLine("nested generic import: a nested type takes its outer's parameters through the index passed");
    }
}
