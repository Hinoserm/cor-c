using System.Globalization;
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
    private readonly bool managed;

    private ProjectEvaluator(string path, string configuration, string? framework, bool managed)
    {
        this.managed = managed;
        project = Path.GetFullPath(path); root = Path.GetDirectoryName(project)!;
        properties["Configuration"] = configuration; globals.Add("Configuration");
        properties["Platform"] = "AnyCPU";
        properties["AssemblyName"] = Path.GetFileNameWithoutExtension(project);
        properties["OutputType"] = "Library";
        properties["DefineConstants"] = configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? "TRACE;DEBUG" : "TRACE";
        properties["EnableDefaultItems"] = "true";
        properties["EnableDefaultCompileItems"] = "true";
        properties["MSBuildProjectFullPath"] = project;
        properties["MSBuildProjectDirectory"] = root;
        properties["MSBuildProjectFile"] = Path.GetFileName(project);
        properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(project);
        if (framework is not null) { properties["TargetFramework"] = framework; globals.Add("TargetFramework"); }
    }

    public static EvaluatedProject Evaluate(string path, string configuration, string? framework, bool managed = false)
        => new ProjectEvaluator(path, configuration, framework, managed).Run();

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
        string assembly = Get("AssemblyName");
        if (assembly.Length == 0 || assembly.IndexOfAny(new[] { '/', '\\' }) >= 0 || assembly is "." or "..")
            throw new InvalidDataException("AssemblyName must be a simple output name");
        if (!managed && Get("CheckForOverflowUnderflow").Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Project-wide checked arithmetic is not yet implemented");
        // THE GLOBAL USINGS the SDK would generate a file of: its implicit
        // ones for Microsoft.NET.Sdk, then the <Using> items, in order, less
        // any a <Using Remove> took away.
        List<string> usings = new();
        if (Get("ImplicitUsings") is "enable" or "true")
            usings.AddRange(new[] { "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks" });
        foreach (string written in _usingItems) if (!usings.Contains(written)) usings.Add(written);
        foreach (string removed in _removedUsings) usings.Remove(removed);
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
            Usings = usings.ToArray(),
            RuntimeKnobs = _knobItems.Select(knob => knob.Name + "=" + knob.Value).ToArray(),
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
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
                        if (Condition(property) && !globals.Contains(property.Name.LocalName))
                        {
                            if (property.HasElements) throw new InvalidDataException("Nested XML in project property " + property.Name);
                            properties[property.Name.LocalName] = Expand(property.Value);
                        }
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
    /// <summary>&lt;Using&gt; items as written: a namespace, or `Alias=Name`.</summary>
    private readonly List<string> _usingItems = new();
    private readonly List<string> _removedUsings = new();
    /// <summary>&lt;RuntimeHostConfigurationOption&gt; items: a setting's name and its value, in order.</summary>
    private readonly List<(string Name, string Value)> _knobItems = new();

    private void EvaluateItem(XElement node)
    {
        if (!Condition(node)) return;
        string type = node.Name.LocalName;

        // A USING IS A NAME, NOT A PATH: `<Using Include="System.Text" />`,
        // with Alias="" to name it, or Static="true" for a type's members.
        if (type == "Using")
        {
            string? name = (string?)node.Attribute("Include");
            string? gone = (string?)node.Attribute("Remove");
            if (gone is not null) { foreach (string one in Split(Expand(gone))) _removedUsings.Add(one); return; }
            if (name is null) throw new InvalidDataException("A <Using> needs Include or Remove");
            string? alias = (string?)node.Attribute("Alias") ?? node.Elements().FirstOrDefault(e => e.Name.LocalName == "Alias")?.Value;
            foreach (string one in Split(Expand(name))) _usingItems.Add(alias is { Length: > 0 } ? alias + "=" + one : one);
            return;
        }
        // A RUNTIME SETTING IS A NAME AND A VALUE, NOT A PATH:
        // `<RuntimeHostConfigurationOption Include="System.GC.HeapHardLimit"
        // Value="33554432" />`, which the SDK writes into runtimeconfig.json
        // and this build bakes into the program (--runtime-knob). The value is
        // what the program reads back under the SDK: a bool's text as "true"
        // or "false", an int's as its number (" 007" is "7"), anything else as
        // written, and the last <Value> of several. The last of a name given
        // twice is the one, as the SDK's dictionary keeps it; Remove and
        // Update find a name in any case, as MSBuild's items do.
        if (type == "RuntimeHostConfigurationOption")
        {
            string? name = (string?)node.Attribute("Include");
            string? gone = (string?)node.Attribute("Remove");
            string? changed = (string?)node.Attribute("Update");
            if ((name is null ? 0 : 1) + (gone is null ? 0 : 1) + (changed is null ? 0 : 1) != 1)
                throw new InvalidDataException("A <RuntimeHostConfigurationOption> needs exactly one Include, Remove or Update");
            if (gone is not null)
            {
                foreach (string one in Split(Expand(gone))) _knobItems.RemoveAll(knob => knob.Name.Equals(one, StringComparison.OrdinalIgnoreCase));
                return;
            }
            string? written = (string?)node.Attribute("Value") ?? node.Elements().LastOrDefault(e => e.Name.LocalName == "Value" && Condition(e))?.Value;
            string value = Expand(written ?? "");
            if (bool.TryParse(value, out bool on)) value = on ? "true" : "false";
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) value = number.ToString(CultureInfo.InvariantCulture);
            if (changed is not null)
            {
                // An Update that names no value leaves the one there.
                if (written is null) return;
                foreach (string one in Split(Expand(changed)))
                    for (int i = 0; i < _knobItems.Count; i++)
                        if (_knobItems[i].Name.Equals(one, StringComparison.OrdinalIgnoreCase)) _knobItems[i] = (_knobItems[i].Name, value);
                return;
            }
            foreach (string one in Split(Expand(name!)))
            {
                _knobItems.RemoveAll(knob => knob.Name == one);
                _knobItems.Add((one, value));
            }
            return;
        }
        // INTERNALS VISIBLE TO ANOTHER ASSEMBLY say nothing to a build that
        // makes one image of every project: a test project's access to the
        // linker's internals (linker.csproj) is the dotnet build's business.
        if (type == "InternalsVisibleTo") return;
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
        {
            foreach (XAttribute attribute in node.Attributes())
                if (attribute.Name.LocalName is not ("Include" or "Exclude" or "Remove" or "Update" or "Condition"))
                    item.Metadata[attribute.Name.LocalName] = Expand(attribute.Value);
            foreach (XElement metadata in node.Elements())
                if (Condition(metadata)) item.Metadata[metadata.Name.LocalName] = Expand(metadata.Value);
        }
        if (type == "ProjectReference" && selected.Any(item => item.Metadata.Count != 0))
            throw new InvalidDataException("ProjectReference metadata is not yet implemented");
    }
}
