using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Corsac.Build;

public sealed class BuildManifest
{
    public required string File { get; init; }
    public string Root => Path.GetDirectoryName(File)!;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Components { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BuildTarget> Targets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal);
    public string DefaultTarget { get; private set; } = "all";

    public static string Discover(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "corsac.build");
            if (System.IO.File.Exists(path)) return path;
        }
        throw new BuildException("No corsac.build in this directory or its parents");
    }

    public static BuildManifest Load(string path, BuildOptions options)
    {
        BuildManifest result = new() { File = Path.GetFullPath(path) };
        using XmlReader reader = XmlReader.Create(result.File, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XElement root = XDocument.Load(reader, LoadOptions.SetLineInfo).Root
            ?? throw new BuildException("Empty build manifest");
        Check(root, "Build", "FormatVersion", "DefaultTargets", "StrictProperties");
        if ((string?)root.Attribute("FormatVersion") != "1") Fail(root, "FormatVersion must be 1");
        string strict = (string?)root.Attribute("StrictProperties") ?? "false";
        if (strict is not ("true" or "false")) Fail(root, "StrictProperties must be true or false");
        if (strict == "true")
        {
            HashSet<string> declared = root.Elements("PropertyGroup").Elements().Select(p => p.Name.LocalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            declared.Add("Configuration");
            foreach (string name in options.Properties.Keys)
                if (!declared.Contains(name)) Fail(root, "Unknown build property " + name);
        }
        result.Properties["Configuration"] = "Release";
        foreach (var pair in options.Properties) result.Properties[pair.Key] = pair.Value;
        result.Properties["WorkspaceDirectory"] = result.Root;
        result.Properties["ManifestDirectory"] = result.Root;
        result.Properties["OutputDirectory"] = Path.Combine(result.Root, "build");
        result.Properties["Jobs"] = options.Jobs.ToString();
        foreach (XElement element in root.Elements())
        {
            switch (element.Name.ToString())
            {
                case "PropertyGroup":
                    Check(element, "PropertyGroup");
                    foreach (XElement property in element.Elements())
                    {
                        if (property.HasAttributes || property.HasElements || property.Name.Namespace != XNamespace.None)
                            Fail(property, "Only simple orchestration properties are currently supported");
                        string name = property.Name.LocalName;
                        if (name is "WorkspaceDirectory" or "ManifestDirectory" or "OutputDirectory" or "Jobs")
                            Fail(property, "Reserved property " + name);
                        if (!options.Properties.ContainsKey(name)) result.Properties[name] = result.Expand(property.Value);
                    }
                    break;
                case "Components":
                    Check(element, "Components");
                    foreach (XElement project in element.Elements())
                    {
                        Check(project, "Project", "Name", "Path");
                        string name = Required(project, "Name");
                        ValidateName(project, name);
                        if (!result.Components.TryAdd(name, result.FullPath(Required(project, "Path"))))
                            Fail(project, "Duplicate component " + name);
                    }
                    break;
                case "Directory":
                    Check(element, "Directory", "Path", "DefaultTargets");
                    if (!result.Defaults.TryAdd(result.FullPath(Required(element, "Path")), Required(element, "DefaultTargets")))
                        Fail(element, "Duplicate directory default");
                    break;
                case "Target": result.AddTarget(element, ""); break;
                default: Fail(element, "Unsupported declaration " + element.Name); break;
            }
        }
        result.DefaultTarget = (string?)root.Attribute("DefaultTargets") ?? "all";
        foreach (BuildTarget target in result.Targets.Values)
        {
            foreach (string reference in Split((string?)target.Element.Attribute("DependsOnTargets")))
                target.Dependencies.Add(result.Resolve(target.Path, reference));
            foreach (string reference in Split((string?)target.Element.Attribute("Steps")))
                target.Steps.Add(result.Resolve(target.Path, reference));
        }
        return result;
    }

    private void AddTarget(XElement element, string parent)
    {
        Check(element, "Target", "Name", "DependsOnTargets", "Steps", "Description", "AllowEmpty");
        string name = Required(element, "Name");
        ValidateName(element, name);
        string path = parent.Length == 0 ? name : parent + "/" + name;
        BuildTarget target = new() { Path = path, Element = element };
        if (!Targets.TryAdd(path, target)) Fail(element, "Duplicate target " + path);
        foreach (XElement child in element.Elements("Target")) AddTarget(child, path);
        if (element.Elements("Finally").Count() > 1) Fail(element, "Multiple Finally blocks");
        if (!target.Tasks.Any() && element.Attribute("Steps") is null && element.Attribute("DependsOnTargets") is null
            && (string?)element.Attribute("AllowEmpty") != "true")
            Fail(element, "Empty target must declare AllowEmpty=true");
    }

    public BuildTarget Resolve(string scope, string reference)
    {
        if (reference.StartsWith('/'))
        {
            if (Targets.TryGetValue(reference[1..], out BuildTarget? exact)) return exact;
        }
        else
        {
            string current = scope;
            while (true)
            {
                string candidate = current.Length == 0 ? reference : current + "/" + reference;
                if (Targets.TryGetValue(candidate, out BuildTarget? target)) return target;
                if (current.Length == 0) break;
                int slash = current.LastIndexOf('/');
                current = slash < 0 ? "" : current[..slash];
            }
        }
        throw new BuildException("Unknown target '" + reference + "' from /" + scope);
    }

    public BuildTarget Select(string? name, string currentDirectory)
    {
        if (name is not null) return Resolve("", name);
        string directory = Path.GetFullPath(currentDirectory);
        foreach (var entry in Defaults.OrderByDescending(p => p.Key.Length))
            if (directory == entry.Key || directory.StartsWith(entry.Key + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return Resolve("", entry.Value);
        return Resolve("", DefaultTarget);
    }

    public string Expand(string text) => Regex.Replace(text, @"\$\(([^)]+)\)", match =>
        Properties.TryGetValue(match.Groups[1].Value, out string? value) ? value
        : throw new BuildException("Undefined property " + match.Value));

    public string FullPath(string value) => Path.GetFullPath(Expand(value), Root);
    public static IEnumerable<string> Split(string? text) => (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static string Required(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value ? value
        : throw new BuildException(Location(element) + "Missing " + name);

    public static void Check(XElement element, string name, params string[] attributes)
    {
        if (element.Name != name) Fail(element, "Expected " + name + ", got " + element.Name);
        foreach (XAttribute attribute in element.Attributes())
            if (!attributes.Contains(attribute.Name.ToString())) Fail(element, "Unsupported attribute " + attribute.Name);
        if (element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            Fail(element, "Unexpected text");
    }

    private static void ValidateName(XElement element, string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+$")) Fail(element, "Invalid name " + name);
    }
    public static void Fail(XElement element, string message) => throw new BuildException(Location(element) + message);
    private static string Location(XElement element) => "line " + ((IXmlLineInfo)element).LineNumber + ": ";
}
