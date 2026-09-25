using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

/// Main(string[] args) is handed its arguments through
/// Environment.GetCommandLineArgs, which the entry calls though the program
/// never names Environment. Compiled against a library's index, the
/// declaration has to be asked for all the same, or the entry finds no
/// Environment and hands Main an empty array.
public static class EntryArgsImportTests
{
    public static void Run(string work)
    {
        string library = Path.Combine(work, "EntryEnvironment.cor");
        string caller = Path.Combine(work, "EntryCaller.cor");
        string index = Path.Combine(work, "entry-environment.idx");
        File.WriteAllText(library, "public static class Environment { public static string[] GetCommandLineArgs() { return new string[1]; } }");
        SourceIndexBuilder.Write(index, new[] { library }, "EntryEnvironment");
        File.WriteAllText(caller, "public static class Program { public static int Main(string[] args) { return args.Length; } }");
        using (IndexedDeclarations declarations = new(index, "EntryEnvironment", new[] { caller }))
        {
            var compiled = Frontend.Compile(new[] { caller }, "entry-args", true, declarations: declarations)
                ?? throw new Exception("a Main taking its arguments did not compile against an index");
            if (!compiled.Bound.Types.ContainsKey("Environment"))
                throw new Exception("Main(string[]) did not bring Environment in from the index; its arguments would be empty");
        }
        Console.WriteLine("entry arguments: Main(string[]) brings Environment in from the index");
    }
}
