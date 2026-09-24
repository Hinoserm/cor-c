using Corsac.Lang.Metadata;

namespace Corsac;

public static class IndexCommand
{
    public static int Run(string[] args)
    {
        string? output = null, assembly = null;
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
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException("Unknown index option: " + args[i]);
                    paths.Add(args[i]); break;
            }
        }
        if (output is null || assembly is null || paths.Count == 0)
            throw new ArgumentException("index requires --assembly <identity>, source files and -o <index>");
        global::Corsac.Lang.Parser.ProjectUsings = usings;
        SourceIndexBuilder.Write(output, paths, assembly, symbols, librarySource: Driver.IsLibrarySource);
        using DeclarationIndex index = new(output);
        Console.Error.WriteLine(output + ": " + index.Count + " index records; implementation bodies omitted");
        return 0;
    }
}
