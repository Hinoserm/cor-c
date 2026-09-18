using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Corsac.Build;

/// <summary>Timestamp-based incremental state for explicitly declared file tasks.</summary>
public sealed class IncrementalTask
{
    private readonly string[] inputs;
    private readonly string[] outputs;
    private readonly string stamp;
    private readonly string initialInputs;

    private IncrementalTask(string[] inputs, string[] outputs, string stamp)
    {
        this.inputs = inputs;
        this.outputs = outputs;
        this.stamp = stamp;
        initialInputs = Snapshot(inputs, required: true);
    }

    public static void Validate(XElement task)
    {
        bool hasInputs = task.Attribute("Inputs") is not null;
        bool hasOutputs = task.Attribute("Outputs") is not null;
        if (hasInputs != hasOutputs) BuildManifest.Fail(task, "Inputs and Outputs must be declared together");
        if (!hasInputs) return;
        foreach (string name in new[] { "Inputs", "Outputs" })
            if (string.IsNullOrWhiteSpace((string?)task.Attribute(name)))
                BuildManifest.Fail(task, name + " cannot be empty");
    }

    public static IncrementalTask? Create(BuildManifest manifest, BuildTarget target, XElement task)
    {
        if (task.Attribute("Inputs") is null) return null;
        string[] Paths(string attribute) => manifest.Expand((string)task.Attribute(attribute)!)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path =>
            {
                if (path.IndexOfAny(new[] { '*', '?' }) >= 0)
                    throw new BuildException("Incremental paths must be explicit files; glob expansion is not implemented: " + path);
                return manifest.FullPath(path);
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        List<string> inputs = Paths("Inputs").ToList();
        string[] outputs = Paths("Outputs");
        if (inputs.Count == 0 || outputs.Length == 0) throw new BuildException("Inputs and Outputs must name files");
        if (task.Attribute("File") is { } script) inputs.Add(manifest.FullPath(script.Value));
        // Tool files are dependencies too. PATH lookup mirrors the common
        // executable case; wrappers must declare additional tool payloads.
        string tool = manifest.Expand((string?)task.Attribute("Executable") ?? (string?)task.Attribute("Interpreter") ?? "");
        if (tool.Contains('/') || tool.Contains('\\')) inputs.Add(manifest.FullPath(tool));
        else
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(directory, tool);
                if (File.Exists(candidate)) { inputs.Add(Path.GetFullPath(candidate)); break; }
            }
        string[] sources = inputs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sources.Intersect(outputs, StringComparer.Ordinal).Any())
            throw new BuildException("Incremental inputs and outputs must be different files");
        XElement expanded = new(task);
        foreach (XElement element in expanded.DescendantsAndSelf())
            foreach (XAttribute attribute in element.Attributes()) attribute.Value = manifest.Expand(attribute.Value);
        // Script bodies are literal script text; $(...) there may be shell
        // substitution rather than an orchestration property.
        string identity = manifest.File + "\n" + target.Path + "\n" + expanded.ToString(SaveOptions.DisableFormatting);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new IncrementalTask(sources, outputs, Path.Combine(manifest.Root, "build", "state", key + ".stamp"));
    }

    public bool IsCurrent()
    {
        if (outputs.Any(path => !File.Exists(path)) || !File.Exists(stamp)) return false;
        DateTime oldestOutput = outputs.Select(File.GetLastWriteTimeUtc).Min();
        if (inputs.Any(path => File.GetLastWriteTimeUtc(path) > oldestOutput)) return false;
        return File.ReadAllText(stamp) == initialInputs + "\n" + Snapshot(outputs, required: true);
    }

    public void RecordSuccess()
    {
        if (Snapshot(inputs, required: true) != initialInputs)
            throw new BuildException("An incremental input changed during execution; output is not recorded as current");
        string state = initialInputs + "\n" + Snapshot(outputs, required: true);
        Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
        string temporary = stamp + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, state); File.Move(temporary, stamp, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Invalidate()
    {
        if (File.Exists(stamp)) File.Delete(stamp);
    }

    private static string Snapshot(IEnumerable<string> paths, bool required)
    {
        List<string> records = new();
        foreach (string path in paths)
        {
            FileInfo file = new(path);
            if (!file.Exists && required) throw new BuildException("Missing incremental file: " + path);
            records.Add(path);
            records.Add(file.LastWriteTimeUtc.Ticks.ToString());
            records.Add(file.Length.ToString());
        }
        return JsonSerializer.Serialize(records);
    }
}
