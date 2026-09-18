namespace Corsac.Tests.Metadata;

internal static class RuntimeDeclarations
{
    public static string[] Sources()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string list = Path.Combine(directory.FullName, "tests/integration/managed-runtime.sources");
            if (File.Exists(list)) return File.ReadAllLines(list).Select(path => Path.Combine(directory.FullName, path)).ToArray();
        }
        throw new DirectoryNotFoundException("Cannot locate real runtime declarations for metadata fixtures");
    }
}
