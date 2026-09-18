namespace Corsac.Build;

public sealed class BuildOptions
{
    public string? File { get; private set; }
    public string? Target { get; private set; }
    public string Toolchain { get; private set; } = "active";
    public int Jobs { get; private set; } = 1;
    public bool List { get; private set; }
    public bool Plan { get; private set; }
    public bool Help { get; private set; }
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static BuildOptions Parse(string[] args)
    {
        BuildOptions result = new();
        for (int i = 0; i < args.Length; i++)
        {
            string Value()
            {
                if (++i == args.Length) throw new BuildException("Missing value for " + args[i - 1]);
                return args[i];
            }
            switch (args[i])
            {
                case "--file": result.File = Value(); break;
                case "--toolchain": result.Toolchain = Value(); break;
                case "--configuration": result.Properties["Configuration"] = Value(); break;
                case "--jobs":
                    if (!int.TryParse(Value(), out int jobs) || jobs < 1 || jobs > 256)
                        throw new BuildException("--jobs must be between 1 and 256");
                    result.Jobs = jobs;
                    break;
                case "--property":
                    string property = Value();
                    int equals = property.IndexOf('=');
                    if (equals < 1) throw new BuildException("--property expects Name=Value");
                    result.Properties[property[..equals]] = property[(equals + 1)..];
                    break;
                case "--list": result.List = true; break;
                case "--plan": result.Plan = true; break;
                case "--help": case "-h": result.Help = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new BuildException("Unsupported option " + args[i]);
                    if (result.Target is not null) throw new BuildException("Specify one target path");
                    result.Target = args[i];
                    break;
            }
        }
        return result;
    }
}
