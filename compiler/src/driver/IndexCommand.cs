using Corsac.Lang.Metadata;

namespace Corsac;

public static class IndexCommand
{
    public static int Run(string[] args)
    {
        string? output = null, assembly = null, on = null;
        List<string> paths = new(), symbols = new(), usings = new();
        for (int i = 0; i < args.Length; i++)
        {
            string Value()
            {
                if (++i >= args.Length) throw new ArgumentException("Missing index option value");
                return args[i];
            }
            switch (args[i])
            {
                case "-o": output = Value(); break;
                case "--assembly": assembly = Value(); break;
                case "-D": case "--define":
                    symbols.AddRange(Value().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "--using": usings.Add(Value()); break;
                // A KERNEL MODULE'S INDEX: its own sources indexed on top of
                // the kernel's index, which keeps answering for the kernel's
                // declarations (DeclarationIndex.Under) and whose hash stays
                // the build stamp.
                case "--on": on = Value(); break;
                // The ring the compiles against this index are for: a class
                // marked for another is not in it (Parser.Ring).
                case "--ring":
                    if (!int.TryParse(Value(), out int ring) || ring < 0 || ring > 3) throw new ArgumentException("--ring is 0, 1, 2 or 3");
                    global::Corsac.Lang.Parser.Ring = ring;
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException("Unknown index option: " + args[i]);
                    paths.Add(args[i]); break;
            }
        }
        if (output is null || assembly is null || paths.Count == 0)
            throw new ArgumentException("index requires --assembly <identity>, source files and -o <index>");
        global::Corsac.Lang.Parser.ProjectUsings = usings;
        if (on is not null && !File.Exists(on)) throw new ArgumentException("--on names the index this one is made on, and " + on + " is not there");
        SourceIndexBuilder.Write(output, paths, assembly, symbols, librarySource: Driver.IsLibrarySource, on: on);
        using DeclarationIndex index = new(output);
        Console.Error.WriteLine(output + ": " + index.Count + " index records; implementation bodies omitted");
        return 0;
    }
}
