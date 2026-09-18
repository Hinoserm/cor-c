using System.Globalization;
using System.Xml.Linq;

namespace Corsac.Build;

public sealed class TaskExecutor
{
    private readonly BuildManifest manifest;
    private readonly BuildOptions options;
    private readonly ProcessRunner runner;
    private readonly TestReport report;

    public TaskExecutor(BuildManifest manifest, BuildOptions options, ProcessRunner runner, TestReport report)
    {
        this.manifest = manifest;
        this.options = options;
        this.runner = runner;
        this.report = report;
    }

    public void Validate(XElement task, bool planning)
    {
        string name = task.Name.ToString();
        switch (name)
        {
            case "Message": case "Error":
                BuildManifest.Check(task, name, "Text");
                manifest.Expand(BuildManifest.Required(task, "Text"));
                if (task.HasElements) BuildManifest.Fail(task, "Unexpected child");
                return;
            case "Compile":
                BuildManifest.Check(task, name, "Project", "Toolchain", "Configuration", "Timeout");
                string project = Project(task);
                if (!File.Exists(project)) BuildManifest.Fail(task, "Project does not exist: " + project);
                if (task.HasElements) BuildManifest.Fail(task, "Unexpected child");
                string toolchain = manifest.Expand((string?)task.Attribute("Toolchain") ?? options.Toolchain);
                if (!planning && toolchain != "dotnet")
                    BuildManifest.Fail(task, "Native project provider is not implemented yet. Use --toolchain dotnet explicitly for host builds; bootstrap is not yet accepted.");
                break;
            case "Exec": case "Test":
                BuildManifest.Check(task, name, "Executable", "WorkingDirectory", "Timeout", "ExpectedExitCode", "Name");
                manifest.Expand(BuildManifest.Required(task, "Executable"));
                break;
            case "Script":
                BuildManifest.Check(task, name, "Interpreter", "File", "WorkingDirectory", "Timeout", "ExpectedExitCode");
                manifest.Expand(BuildManifest.Required(task, "Interpreter"));
                if ((task.Attribute("File") is null) == (task.Element("Body") is null))
                    BuildManifest.Fail(task, "Script needs exactly one File or Body");
                if (task.Elements("Body").Count() > 1) BuildManifest.Fail(task, "Multiple script bodies");
                if (task.Attribute("File") is { } script && !File.Exists(manifest.FullPath(script.Value)))
                    BuildManifest.Fail(task, "Missing script " + script.Value);
                break;
            default: BuildManifest.Fail(task, "Unsupported task " + task.Name); break;
        }
        foreach (XElement child in task.Elements())
        {
            if (child.Name == "Argument")
            {
                BuildManifest.Check(child, "Argument", "Value");
                manifest.Expand(BuildManifest.Required(child, "Value"));
            }
            else if (child.Name == "Environment")
            {
                BuildManifest.Check(child, "Environment", "Name", "Value");
                BuildManifest.Required(child, "Name");
                manifest.Expand(BuildManifest.Required(child, "Value"));
            }
            else if (child.Name == "Body" && name == "Script")
            {
                if (child.HasElements || child.HasAttributes) BuildManifest.Fail(child, "Body must be text");
            }
            else BuildManifest.Fail(child, "Unexpected child " + child.Name);
            if (child.HasElements) BuildManifest.Fail(child, "Unexpected nested element");
        }
        if (task.Elements("Environment").GroupBy(e => (string?)e.Attribute("Name")).Any(g => g.Count() > 1))
            BuildManifest.Fail(task, "Duplicate environment variable");
        Timeout(task);
        if (!int.TryParse((string?)task.Attribute("ExpectedExitCode") ?? "0", out _))
            BuildManifest.Fail(task, "ExpectedExitCode must be an integer");
    }

    public async Task Execute(BuildTarget target, XElement task, CancellationToken cancel)
    {
        if (task.Name == "Message") { Console.WriteLine(manifest.Expand(BuildManifest.Required(task, "Text"))); return; }
        if (task.Name == "Error") throw new BuildException(manifest.Expand(BuildManifest.Required(task, "Text")));
        List<string> args = task.Elements("Argument").Select(e => manifest.Expand(BuildManifest.Required(e, "Value"))).ToList();
        Dictionary<string, string> environment = task.Elements("Environment").ToDictionary(
            e => BuildManifest.Required(e, "Name"), e => manifest.Expand(BuildManifest.Required(e, "Value")));
        string directory = manifest.FullPath((string?)task.Attribute("WorkingDirectory") ?? ".");
        string executable;
        string? temporary = null;
        if (task.Name == "Compile")
        {
            executable = "dotnet";
            args = new List<string> { "build", Project(task), "--nologo", "-m:1", "-c",
                manifest.Expand((string?)task.Attribute("Configuration") ?? "$(Configuration)"),
                "-p:UseSharedCompilation=false" };
            // External compilers get one worker. The graph runner owns the budget.
            environment["DOTNET_PROCESSOR_COUNT"] = "1";
        }
        else if (task.Name == "Script")
        {
            executable = manifest.Expand(BuildManifest.Required(task, "Interpreter"));
            string script;
            if (task.Attribute("File") is { } file) script = manifest.FullPath(file.Value);
            else
            {
                temporary = Path.GetTempFileName();
                File.WriteAllText(temporary, task.Element("Body")!.Value);
                script = temporary;
            }
            args.Insert(0, script);
        }
        else executable = manifest.Expand(BuildManifest.Required(task, "Executable"));
        if (executable.Contains('/') || executable.Contains('\\')) executable = manifest.FullPath(executable);
        bool isTest = task.Name == "Test";
        string testName = (string?)task.Attribute("Name") ?? target.Path;
        ProcessResult? result = null;
        try
        {
            result = await runner.Run(target.Path, executable, args, directory, environment, Timeout(task), cancel);
            int expected = int.Parse((string?)task.Attribute("ExpectedExitCode") ?? "0", CultureInfo.InvariantCulture);
            bool passed = !result.TimedOut && result.ExitCode == expected;
            string detail = executable + (result.TimedOut ? " timed out" : " exited " + result.ExitCode + ", expected " + expected)
                + "; logs: " + result.LogPrefix;
            if (isTest) report.Add(new TestResult { Target = target.Path, Name = testName,
                Status = result.TimedOut ? "timed-out" : passed ? "passed" : "failed", Detail = detail, Process = result });
            if (!passed) throw new BuildException(detail);
        }
        catch (Exception error)
        {
            if (isTest && result is null) report.Add(new TestResult { Target = target.Path, Name = testName,
                Status = "error", Detail = error.Message });
            throw;
        }
        finally { if (temporary is not null) File.Delete(temporary); }
    }

    private string Project(XElement task)
    {
        string name = manifest.Expand(BuildManifest.Required(task, "Project"));
        return manifest.Components.TryGetValue(name, out string? path) ? path : manifest.FullPath(name);
    }

    private static TimeSpan Timeout(XElement task)
    {
        if (!TimeSpan.TryParse((string?)task.Attribute("Timeout") ?? "01:00:00", CultureInfo.InvariantCulture, out TimeSpan timeout)
            || timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new BuildException("Invalid Timeout at " + task.Name);
        return timeout;
    }
}
