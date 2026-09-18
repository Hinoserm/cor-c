using System.Text.RegularExpressions;

namespace Corsac.Projects;

public static class ProjectGlob
{
    public static bool Matches(string path, string pattern)
    {
        path = Path.GetFullPath(path).Replace('\\', '/');
        pattern = Path.GetFullPath(pattern).Replace('\\', '/');
        string regex = Regex.Escape(pattern).Replace("\\*\\*/", "(?:.*/)?").Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*").Replace("\\?", "[^/]");
        return Regex.IsMatch(path, "^" + regex + "$");
    }

    public static IEnumerable<string> Expand(string root, string pattern)
    {
        string full = Path.GetFullPath(pattern.Replace('\\', '/'), root);
        int wildcard = full.IndexOfAny(new[] { '*', '?' });
        if (wildcard < 0) { yield return full; yield break; }
        string directory = full[..full.LastIndexOf(Path.DirectorySeparatorChar, wildcard)];
        if (!Directory.Exists(directory)) yield break;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            if (Matches(file, full)) yield return file;
    }
}
