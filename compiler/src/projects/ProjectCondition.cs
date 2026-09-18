using System.Globalization;
using System.Text.RegularExpressions;

namespace Corsac.Projects;

/// <summary>Project-condition grammar, independent of the MSBuild engine.</summary>
public sealed class ProjectCondition
{
    private readonly List<string> tokens = new();
    private readonly string directory;
    private int at;

    private ProjectCondition(string text, string directory)
    {
        this.directory = directory;
        int position = 0;
        while (position < text.Length)
        {
            Match token = Regex.Match(text[position..], "^\\s*('([^']*)'|\"([^\"]*)\"|==|!=|<=|>=|[()!,<>]|[A-Za-z0-9_.+\\\\/-]+)");
            if (!token.Success)
            {
                if (text[position..].Trim().Length == 0) break;
                throw new InvalidDataException("Unsupported project condition: " + text);
            }
            tokens.Add(token.Groups[1].Value); position += token.Length;
        }
    }

    public static bool Evaluate(string text, string directory)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        ProjectCondition parser = new(text, directory);
        bool value = parser.Or();
        if (parser.at != parser.tokens.Count) throw new InvalidDataException("Trailing project condition input: " + text);
        return value;
    }

    private bool Take(string token)
    {
        if (at >= tokens.Count || !tokens[at].Equals(token, StringComparison.OrdinalIgnoreCase)) return false;
        at++; return true;
    }
    private string Next() => at < tokens.Count ? tokens[at++] : throw new InvalidDataException("Incomplete project condition");
    private void Need(string token) { if (!Take(token)) throw new InvalidDataException("Expected '" + token + "' in project condition"); }
    private bool Or() { bool value = And(); while (Take("Or")) { bool right = And(); value |= right; } return value; }
    private bool And() { bool value = Atom(); while (Take("And")) { bool right = Atom(); value &= right; } return value; }
    private bool Atom()
    {
        if (Take("!")) return !Atom();
        if (Take("(")) { bool inner = Or(); Need(")"); return inner; }
        string left = Unquote(Next());
        if (Take("("))
        {
            string argument = Unquote(Next()); Need(")");
            if (left.Equals("Exists", StringComparison.OrdinalIgnoreCase))
            {
                string path = Path.GetFullPath(argument.Replace('\\', Path.DirectorySeparatorChar), directory);
                return File.Exists(path) || Directory.Exists(path);
            }
            if (left.Equals("HasTrailingSlash", StringComparison.OrdinalIgnoreCase)) return argument.EndsWith('/') || argument.EndsWith('\\');
            throw new InvalidDataException("Unsupported project condition function: " + left);
        }
        if (at < tokens.Count && tokens[at] is "==" or "!=" or "<" or ">" or "<=" or ">=")
        {
            string operation = Next(), right = Unquote(Next());
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            if (operation is not ("==" or "!="))
            {
                if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                    && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)) comparison = a.CompareTo(b);
                else if (Version.TryParse(left, out Version? av) && Version.TryParse(right, out Version? bv)) comparison = av.CompareTo(bv);
                else throw new InvalidDataException("Ordered condition comparison requires numbers or versions");
            }
            return operation switch { "==" => comparison == 0, "!=" => comparison != 0, "<" => comparison < 0,
                ">" => comparison > 0, "<=" => comparison <= 0, _ => comparison >= 0 };
        }
        if (bool.TryParse(left, out bool flag)) return flag;
        throw new InvalidDataException("Project condition is not a Boolean: " + left);
    }
    private static string Unquote(string value) => value.Length >= 2 && value[0] is '\'' or '"' ? value[1..^1] : value;
}
