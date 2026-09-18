using Corsac.Lang.Elf;

namespace Corsac.LinkerApp;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("corlink <file.o> ... -o <output> [--entry symbol] [--flat] [--base address] [--paddr address] [--no-lto]");
            return 0;
        }
        try { return ObjectLinkCommand.Run(args); }
        catch (LinkException error)
        {
            foreach (string detail in error.Errors) Console.Error.WriteLine("corlink: " + detail);
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("corlink: " + error.Message);
            return 1;
        }
    }
}
