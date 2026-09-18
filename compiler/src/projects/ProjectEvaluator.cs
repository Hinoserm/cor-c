using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Corsac.Projects;

/// <summary>Owned evaluator for the supported standard SDK-style project profile.</summary>
public sealed class ProjectEvaluator
{
    private readonly Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> globals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    private readonly List<(XElement Element, string File)> itemGroups = new();
    private readonly List<ProjectItem> items = new();
    private readonly List<string> inputs = new();
    private readonly string project;
    private readonly string root;

    private ProjectEvaluator(string path, string configuration, string? framework)
    {
        project = Path.GetFullPath(path); root = Path.GetDirectoryName(project)!;
        properties["Configuration"] = configuration; globals.Add("Configuration");
        properties["Platform"] = "AnyCPU";
        properties["AssemblyName"] = Path.GetFileNameWithoutExtension(project);
        properties["OutputType"] = "Library";
        properties["EnableDefaultItems"] = "true";
        properties["EnableDefaultCompileItems"] = "true";
        properties["MSBuildProjectFullPath"] = project;
        properties["MSBuildProjectDirectory"] = root;
        properties["MSBuildProjectFile"] = Path.GetFileName(project);
        properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(project);
        if (framework is not null) { properties["TargetFramework"] = framework; globals.Add("TargetFramework"); }
    }

    public static EvaluatedProject Evaluate(string path, string configuration, string? framework)
        => new ProjectEvaluator(path, configuration, framework).Run();

    private EvaluatedProject Run()
    {
        using (XmlReader reader = Reader(project))
        {
            XElement document = XDocument.Load(reader).Root ?? throw new InvalidDataException("Empty project: " + project);
            if (document.Name.LocalName != "Project" || (string?)document.Attribute("Sdk") != "Microsoft.NET.Sdk")
                throw new InvalidDataException("Only Microsoft.NET.Sdk projects are currently supported: " + project);
        }
        string? props = FindDirectoryFile("Directory.Build.props");
        if (props is not null) Load(props);
        Load(project);
        string? targets = FindDirectoryFile("Directory.Build.targets");
        if (targets is not null) Load(targets);
        if (Get("EnableDefaultItems") != "false" && Get("EnableDefaultCompileItems") != "false")
            foreach (string file in ProjectGlob.Expand(root, "**/*.cs"))
                if (!Path.GetRelativePath(root, file).Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj" or ".git"))
                    items.Add(new ProjectItem { Type = "Compile", Include = file });
        foreach (var group in itemGroups)
        {
            ThisFile(group.File);
            if (!Condition(group.Element)) continue;
            foreach (XElement item in group.Element.Elements()) EvaluateItem(item);
        }
        foreach (ProjectItem item in items)
            if (item.Type is "PackageReference" or "Reference" or "Analyzer" or "EmbeddedResource")
                throw new InvalidDataException("Unsupported active project item: " + item.Type + " in " + project);
        string framework = Get("TargetFramework");
        if (framework.Length == 0) throw new InvalidDataException("Select a target framework for " + project);
        if (Get("CheckForOverflowUnderflow").Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Project-wide checked arithmetic is not yet implemented");
        string[] sources = items.Where(item => item.Type == "Compile").Select(item => item.Include).ToArray();
        if (sources.Distinct(StringComparer.Ordinal).Count() != sources.Length) throw new InvalidDataException("Duplicate Compile items in " + project);
        List<string> defines = Split(Get("DefineConstants")).ToList();
        if (Get("DisableImplicitFrameworkDefines") != "true")
        {
            Match net = Regex.Match(framework, "^net([5-9]|[1-9][0-9]+)\\.([0-9]+)$");
            if (!net.Success) throw new InvalidDataException("Unsupported native framework profile: " + framework);
            int major = int.Parse(net.Groups[1].Value);
            defines.Add("NET"); defines.Add("NETCOREAPP"); defines.Add("NET" + major + "_" + net.Groups[2].Value);
            for (int version = 5; version <= major; version++) defines.Add("NET" + version + "_0_OR_GREATER");
        }
        return new EvaluatedProject { Path = project, AssemblyName = Get("AssemblyName"), OutputType = Get("OutputType"),
            Framework = framework, StartupObject = Get("StartupObject"), Sources = sources,
            References = items.Where(item => item.Type == "ProjectReference").Select(item => item.Include).ToArray(),
            Defines = defines.Distinct(StringComparer.Ordinal).ToArray(), WarningsAsErrors = Get("TreatWarningsAsErrors") == "true",
            Evaluation = string.Join("\n", inputs.Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks)) };
    }

    private static XmlReader Reader(string path) => XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    private string Get(string name) => properties.GetValueOrDefault(name, "");
    private string Expand(string text)
    {
        if (text.Contains("$([", StringComparison.Ordinal)) throw new InvalidDataException("Project property functions are not yet supported");
        string expanded = Regex.Replace(text, @"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)", match => Get(match.Groups[1].Value));
        expanded = Regex.Replace(expanded, @"@\(([A-Za-z_][A-Za-z0-9_.-]*)\)", match => string.Join(";", items.Where(item => item.Type == match.Groups[1].Value).Select(item => item.Include)));
        if (expanded.Contains("$(") || expanded.Contains("@(") || expanded.Contains("%("))
            throw new InvalidDataException("Unsupported project expansion: " + text);
        return expanded;
    }
    private bool Condition(XElement node) => ProjectCondition.Evaluate(Expand((string?)node.Attribute("Condition") ?? ""), root);
    private static IEnumerable<string> Split(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private string? FindDirectoryFile(string name)
    {
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
    private void ThisFile(string path)
    {
        properties["MSBuildThisFileFullPath"] = path;
        properties["MSBuildThisFileDirectory"] = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
        properties["MSBuildThisFileName"] = Path.GetFileNameWithoutExtension(path);
        properties["MSBuildThisFileExtension"] = Path.GetExtension(path);
        properties["MSBuildThisFile"] = Path.GetFileName(path);
    }
    private void Load(string path)
    {
        path = Path.GetFullPath(path);
        if (!active.Add(path)) throw new InvalidDataException("Project import cycle: " + path);
        inputs.Add(path); ThisFile(path);
        using XmlReader reader = Reader(path);
        XElement document = XDocument.Load(reader, LoadOptions.SetLineInfo).Root ?? throw new InvalidDataException("Empty project import: " + path);
        if (document.Name.LocalName != "Project") throw new InvalidDataException("Expected Project in " + path);
        EvaluateNodes(document.Elements(), path);
        active.Remove(path);
    }
    private void EvaluateNodes(IEnumerable<XElement> nodes, string file)
    {
        foreach (XElement node in nodes)
        {
            ThisFile(file);
            if (node.Name.LocalName == "ItemGroup") { itemGroups.Add((node, file)); continue; }
            if (!Condition(node)) continue;
            switch (node.Name.LocalName)
            {
                case "PropertyGroup":
                    foreach (XElement property in node.Elements())
                        if (Condition(property) && !globals.Contains(property.Name.LocalName)) properties[property.Name.LocalName] = Expand(property.Value);
                    break;
                case "Import":
                    string imported = Expand((string?)node.Attribute("Project") ?? throw new InvalidDataException("Import requires Project"));
                    foreach (string path in ProjectGlob.Expand(Path.GetDirectoryName(file)!, imported)) Load(path);
                    break;
                case "ImportGroup": EvaluateNodes(node.Elements(), file); break;
                case "Choose":
                    XElement? branch = node.Elements().FirstOrDefault(child => child.Name.LocalName == "When" && Condition(child))
                        ?? node.Elements().SingleOrDefault(child => child.Name.LocalName == "Otherwise");
                    if (branch is not null) EvaluateNodes(branch.Elements(), file);
                    break;
                default: throw new InvalidDataException("Unsupported active project element " + node.Name + " in " + file);
            }
        }
    }
    private void EvaluateItem(XElement node)
    {
        if (!Condition(node)) return;
        string type = node.Name.LocalName;
        if (type is not ("Compile" or "ProjectReference" or "None" or "Content" or "Using" or "PackageReference" or "Reference" or "Analyzer" or "EmbeddedResource"))
            throw new InvalidDataException("Unsupported active item type: " + type);
        string? include = (string?)node.Attribute("Include"), remove = (string?)node.Attribute("Remove"), update = (string?)node.Attribute("Update");
        if ((include is null ? 0 : 1) + (remove is null ? 0 : 1) + (update is null ? 0 : 1) != 1)
            throw new InvalidDataException("An item needs exactly one Include, Remove or Update");
        string[] patterns = Split(Expand(include ?? remove ?? update!)).Select(pattern => Path.GetFullPath(pattern.Replace('\\', '/'), root)).ToArray();
        if (remove is not null) { items.RemoveAll(item => item.Type == type && patterns.Any(pattern => ProjectGlob.Matches(item.Include, pattern))); return; }
        List<ProjectItem> selected = new();
        if (update is not null) selected.AddRange(items.Where(item => item.Type == type && patterns.Any(pattern => ProjectGlob.Matches(item.Include, pattern))));
        else
        {
            string[] excluded = Split(Expand((string?)node.Attribute("Exclude") ?? "")).Select(pattern => Path.GetFullPath(pattern.Replace('\\', '/'), root)).ToArray();
            foreach (string pattern in patterns)
                foreach (string path in ProjectGlob.Expand(root, pattern))
                    if (!excluded.Any(exclude => ProjectGlob.Matches(path, exclude)))
                    {
                        ProjectItem item = new() { Type = type, Include = path };
                        selected.Add(item); items.Add(item);
                    }
        }
        foreach (ProjectItem item in selected)
            foreach (XElement metadata in node.Elements())
                if (Condition(metadata)) item.Metadata[metadata.Name.LocalName] = Expand(metadata.Value);
        if (type == "ProjectReference" && selected.Any(item => item.Metadata.Count != 0))
            throw new InvalidDataException("ProjectReference metadata is not yet implemented");
    }
}
