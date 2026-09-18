using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class DeclarationCatalogTests
{
    public static void Run(string work)
    {
        string path = Path.Combine(work, "catalog.idx");
        string assembly = SourceIndexBuilder.AssemblyIdentity("Catalog");
        IEnumerable<DeclarationRecord> Records()
        {
            for (int i = 0; i < 100; i++)
                yield return new SourceDeclaration { Key = "T:" + assembly + "\nNs.T" + i,
                    Path = "source.cs", Text = "class T" + i + " {}", Namespace = "Ns", Outer = "",
                    From = 0, To = 10, Line = 1, Column = 1, SourceHash = new byte[32], DeclarationHash = new byte[32], Scope = new FileScope() }.Encode();
        }
        DeclarationIndexWriter.Write(path, Records());
        using DeclarationCatalog catalog = new(path, 4096);
        using (DeclarationLease first = catalog.Acquire("Catalog", "Ns.T0")!)
        {
            using DeclarationLease again = catalog.Acquire("Catalog", "Ns.T0")!;
            Require(ReferenceEquals(first.Records, again.Records), "shared immutable cache entry");
            Require(catalog.PayloadLoads == 1, "namespace was loaded eagerly");
            Require(catalog.Acquire("Catalog", "Ns.Missing") is null, "missing declaration");
        }
        for (int i = 1; i < 100; i++)
        {
            using DeclarationLease lease = catalog.Acquire("Catalog", "Ns.T" + i)!;
            Require(catalog.ResidentBytes <= 4096, "cache exceeded budget");
        }
        long before = catalog.PayloadLoads;
        using (DeclarationLease reloaded = catalog.Acquire("Catalog", "Ns.T0")!)
            Require(catalog.PayloadLoads == before + 1 && reloaded.Records.Single().Key.EndsWith("\nNs.T0"), "eviction changed identity or did not reload");
        List<DeclarationLease> pinned = new();
        bool rejected = false;
        try
        {
            for (int i = 0; i < 100; i++) pinned.Add(catalog.Acquire("Catalog", "Ns.T" + i)!);
        }
        catch (InvalidDataException) { rejected = true; }
        finally { foreach (DeclarationLease lease in pinned) lease.Dispose(); }
        Require(rejected, "unbounded pinned metadata accepted");
        using (DeclarationLease recovered = catalog.Acquire("Catalog", "Ns.T99")!)
            Require(recovered.Records.Count == 1, "cache did not recover after releasing pins");
        DeclarationLease disposed = catalog.Acquire("Catalog", "Ns.T1")!;
        disposed.Dispose(); disposed.Dispose();
        bool useAfterDispose = false;
        try { _ = disposed.Records; } catch (ObjectDisposedException) { useAfterDispose = true; }
        Require(useAfterDispose, "disposed lease remained usable");
        Console.WriteLine("  declaration cache: lazy loads, sharing, bounded eviction, pin exhaustion and recovery passed");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
}
