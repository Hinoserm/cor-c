using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class Program
{
    public static int Main()
    {
        string work = Path.Combine(Path.GetTempPath(), "corc-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            passed++;
        }
        void Invalid(Action action)
        {
            try { action(); } catch (InvalidDataException) { passed++; return; }
            throw new Exception("Malformed index was accepted");
        }
        try
        {
            SyntaxTokenCacheTests.Run();
            DeclarationBatchTests.Run(work);
            SourceIndexTests.Run(work);
            DeclarationCatalogTests.Run(work);
            InterfacePlanTests.Run(work);
            DefinitionSemanticsTests.Run();
            PartialOwnershipTests.Run(work);
            ExportBoundaryTests.Run();
            IrCodecTests.Run();
            InterfaceDispatchTests.Run(work);
            RethrowTests.Run();
            ExtensionLookupTests.Run(work);
            InitializerScopeTests.Run(work);
            ClosureOwnershipTests.Run(work);
            StructuralOwnershipTests.Run(work);
            string path = Path.Combine(work, "declarations.idx");
            DeclarationRecord[] records = Enumerable.Range(0, 1000).Select(i =>
                new DeclarationRecord("Namespace.Type" + i.ToString("D4"), BitConverter.GetBytes(i))).ToArray();
            DeclarationIndexWriter.Write(path, records.Reverse(), 4096);
            byte[] first = File.ReadAllBytes(path);
            using (DeclarationIndex index = new(path))
            {
                Check(index.Count == 1000, "external merge count");
                foreach (int i in new[] { 0, 1, 499, 999 })
                    Check(BitConverter.ToInt32(index.Find(records[i].Key).Single().Payload) == i, "lookup " + i);
                Check(!index.Find("absent").Any(), "missing key");
                Check(index.WithPrefix("Namespace.Type00").Count() == 100, "prefix enumeration");
                Parallel.For(0, 1000, i =>
                {
                    if (BitConverter.ToInt32(index.Find(records[i].Key).Single().Payload) != i)
                        throw new Exception("concurrent lookup");
                });
                Check(true, "concurrent readers");
            }
            DeclarationIndexWriter.Write(path, records, 8192);
            Check(first.SequenceEqual(File.ReadAllBytes(path)), "deterministic across input order and sort budget");
            DeclarationIndexWriter.Write(path, new[] { new DeclarationRecord("Partial", new byte[] { 2 }),
                new DeclarationRecord("Partial", new byte[] { 1 }), new DeclarationRecord("Other", Array.Empty<byte>()) }, 4096);
            using (DeclarationIndex index = new(path))
                Check(index.Find("Partial").Select(r => r.Payload[0]).SequenceEqual(new byte[] { 1, 2 }), "partial fragments retained");
            byte[] valid = File.ReadAllBytes(path);
            Invalid(() => DeclarationIndexWriter.Write(path, new[] { new DeclarationRecord("too-large", new byte[4096]) }, 4096));
            Check(valid.SequenceEqual(File.ReadAllBytes(path)), "failed generation preserves previous index");
            Check(!Directory.EnumerateDirectories(work).Any(), "sort runs cleaned up");
            byte[] damaged = (byte[])valid.Clone();
            damaged[4] = 99; File.WriteAllBytes(path, damaged);
            Invalid(() => { using DeclarationIndex ignored = new(path); });
            damaged = (byte[])valid.Clone();
            Array.Fill(damaged, (byte)255, 16, 8); File.WriteAllBytes(path, damaged);
            Invalid(() => { using DeclarationIndex ignored = new(path); });
            File.WriteAllBytes(path, valid[..^1]);
            Invalid(() => { using DeclarationIndex ignored = new(path); });
            DeclarationIndexWriter.Write(path, new[] { new DeclarationRecord("Value", new byte[] { 42 }) });
            damaged = File.ReadAllBytes(path);
            damaged[32 + 72 + 5] ^= 1; File.WriteAllBytes(path, damaged);
            Invalid(() => { using DeclarationIndex index = new(path); _ = index.Find("Value").ToArray(); });
            DeclarationIndexWriter.Write(path, new[] { new DeclarationRecord("Value", new byte[] { 42 }) });
            damaged = File.ReadAllBytes(path);
            damaged[32 + 72] ^= 1; File.WriteAllBytes(path, damaged);
            Invalid(() => { using DeclarationIndex index = new(path); _ = index.Find("Value").ToArray(); });
            DeclarationIndexWriter.Write(path, Array.Empty<DeclarationRecord>());
            using (DeclarationIndex index = new(path)) Check(index.Count == 0 && !index.WithPrefix("").Any(), "empty index");
            Console.WriteLine($"metadata: {passed} checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(work)) File.Delete(file);
            Directory.Delete(work);
        }
    }
}
