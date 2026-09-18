using System.Xml.Linq;
using Corsac.Build;

namespace Corsac.Build.Tests;

public static class Program
{
    private static int passed;
    private static int failed;
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "corsac-build-tests-" + Guid.NewGuid().ToString("N"));

    public static async Task<int> Main()
    {
        Directory.CreateDirectory(Work);
        Check("bare properties compose with targets without splitting values", () =>
        {
            BuildOptions options = BuildOptions.Parse(["disk=output file.bin", "arch=486", "configure", "smp=0", "label=a=b"]);
            Require(options.Target == "configure" && options.Properties["disk"] == "output file.bin"
                && options.Properties["arch"] == "486" && options.Properties["Smp"] == "0" && options.Properties["label"] == "a=b");
            Require(BuildOptions.Parse(["disk=output.bin"]).Target is null);
            ExpectError(() => BuildOptions.Parse(["=oops"]), "Invalid property");
            ExpectError(() => BuildOptions.Parse(["one", "two"]), "one target");
        });
        Check("default worker budget uses available logical CPUs", () =>
        {
            Require(BuildOptions.Parse([]).Jobs == Environment.ProcessorCount);
            Require(BuildOptions.Parse(["--jobs", "1"]).Jobs == 1);
        });
        Check("incremental timestamp state tracks inputs outputs and commands", () =>
        {
            BuildManifest manifest = Load("<Target Name='all'><Exec Executable='tool' Inputs='input' Outputs='output'/></Target>");
            BuildTarget target = manifest.Targets["all"];
            XElement task = target.Tasks.Single();
            string source = Path.Combine(manifest.Root, "input");
            string output = Path.Combine(manifest.Root, "output");
            File.WriteAllText(source, "one");
            DateTime original = DateTime.UtcNow.AddMinutes(-2);
            File.SetLastWriteTimeUtc(source, original);
            IncrementalTask State() => IncrementalTask.Create(manifest, target, task)!;
            Require(!State().IsCurrent());
            File.WriteAllText(output, "one");
            State().RecordSuccess();
            Require(State().IsCurrent());
            File.SetLastWriteTimeUtc(source, original.AddSeconds(-1));
            Require(!State().IsCurrent()); // A checkout can move timestamps backwards.
            State().RecordSuccess();
            Require(State().IsCurrent());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
            Require(!State().IsCurrent());
            File.SetLastWriteTimeUtc(source, original);
            State().RecordSuccess();
            task.Add(new XElement("Argument", new XAttribute("Value", "new-option")));
            Require(!State().IsCurrent());
            State().RecordSuccess();
            File.Delete(output);
            Require(!State().IsCurrent());
            ExpectError(() => State().RecordSuccess(), "Missing incremental file");
            File.Delete(source);
            ExpectError(() => State(), "Missing incremental file");
        });
        Check("nested lookup and prerequisite ordering", () =>
        {
            BuildManifest manifest = Load("""
              <Target Name="bootstrap" Steps="seed;native">
                <Target Name="seed" AllowEmpty="true" />
                <Target Name="native" DependsOnTargets="seed" AllowEmpty="true" />
              </Target>
              """);
            BuildGraph graph = new(manifest.Resolve("", "bootstrap"));
            Require(string.Join(",", graph.Ordered.Select(t => t.Path)) == "bootstrap/seed,bootstrap/native,bootstrap");
            Require(manifest.Resolve("bootstrap/native", "seed").Path == "bootstrap/seed");
        });
        Check("steps introduce cycle constraints", () =>
        {
            BuildManifest manifest = Load("""
              <Target Name="all" Steps="first;second">
                <Target Name="first" DependsOnTargets="second" />
                <Target Name="second" AllowEmpty="true" />
              </Target>
              """);
            ExpectError(() => new BuildGraph(manifest.Resolve("", "all")), "cycle");
        });
        Check("undefined nested target rejected", () => ExpectError(() => Load("<Target Name='all' Steps='footjuice'/>"), "Unknown target"));
        Check("duplicate target rejected", () => ExpectError(() => Load("<Target Name='all' AllowEmpty='true'/><Target Name='all' AllowEmpty='true'/>"), "Duplicate"));
        Check("unknown declaration rejected", () => ExpectError(() => Load("<Magic/>"), "Unsupported"));
        Check("CLI properties override manifest", () =>
        {
            BuildManifest manifest = Load("<PropertyGroup><Configuration>Debug</Configuration></PropertyGroup><Target Name='all' AllowEmpty='true'/>",
                "--configuration", "Release");
            Require(manifest.Expand("$(Configuration)") == "Release");
        });
        Check("nearest manifest and directory default", () =>
        {
            BuildManifest manifest = Load("<Directory Path='compiler' DefaultTargets='compiler'/><Target Name='compiler' AllowEmpty='true'/>");
            string child = Path.Combine(manifest.Root, "compiler", "src");
            Directory.CreateDirectory(child);
            Require(BuildManifest.Discover(child) == manifest.File);
            Require(manifest.Select(null, child).Path == "compiler");
        });
        Check("native provider cannot silently use .NET", () =>
        {
            BuildManifest manifest = Load("<Target Name='all'><Compile Project='a.csproj'/></Target>");
            File.WriteAllText(Path.Combine(manifest.Root, "a.csproj"), "<Project/>");
            TaskExecutor executor = new(manifest, BuildOptions.Parse([]), new ProcessRunner(1, Work), new TestReport());
            ExpectError(() => executor.Validate(manifest.Targets["all"].Tasks.Single(), false), "Native project provider");
        });
        if (!OperatingSystem.IsWindows())
        {
            await CheckAsync("independent processes overlap and auto workers share a global lease", async () =>
            {
                string directory = Path.Combine(Work, "parallel");
                Directory.CreateDirectory(directory);
                ProcessRunner runner = new(2, Path.Combine(directory, "logs"));
                Task<ProcessResult> First(string own, string other) => runner.Run(own, "sh",
                    ["-c", "touch " + own + "; while [ ! -f " + other + " ]; do sleep 0.01; done; printf %s \"$CORSAC_BUILD_JOBS\""],
                    directory, new Dictionary<string, string>(), TimeSpan.FromSeconds(5), CancellationToken.None);
                ProcessResult[] results = await Task.WhenAll(First("one", "two"), First("two", "one"));
                Require(results.All(r => r.ExitCode == 0 && !r.TimedOut && File.ReadAllText(r.LogPrefix + ".out.log") == "1"));
                ProcessResult all = await runner.Run("all", "sh", ["-c", "printf %s \"$CORSAC_BUILD_JOBS\""],
                    directory, new Dictionary<string, string>(), TimeSpan.FromSeconds(5), CancellationToken.None, true);
                Require(all.ExitCode == 0 && File.ReadAllText(all.LogPrefix + ".out.log") == "2");
            });
            await CheckAsync("successful file tasks skip until an input or output changes", async () =>
            {
                BuildManifest manifest = Load("""
                  <Target Name="all"><Script Interpreter="sh" Inputs="input" Outputs="output"><Body>cp input output; echo "$(printf ran)" &gt;&gt; runs</Body></Script></Target>
                  """);
                string input = Path.Combine(manifest.Root, "input");
                File.WriteAllText(input, "one");
                File.SetLastWriteTimeUtc(input, DateTime.UtcNow.AddMinutes(-2));
                Require(await Corsac.Build.Program.Main(["--file", manifest.File]) == 0);
                Require(await Corsac.Build.Program.Main(["--file", manifest.File]) == 0);
                Require(File.ReadAllLines(Path.Combine(manifest.Root, "runs")).Length == 1);
                File.WriteAllText(input, "changed");
                Require(await Corsac.Build.Program.Main(["--file", manifest.File]) == 0);
                Require(File.ReadAllLines(Path.Combine(manifest.Root, "runs")).Length == 2);
                File.Delete(Path.Combine(manifest.Root, "output"));
                Require(await Corsac.Build.Program.Main(["--file", manifest.File]) == 0);
                Require(File.ReadAllLines(Path.Combine(manifest.Root, "runs")).Length == 3);
            });
            await CheckAsync("literal arguments without shell expansion", async () =>
            {
                ProcessResult result = await new ProcessRunner(1, Path.Combine(Work, "argv")).Run("argv", "printf",
                    ["%s", "a b;$(not-a-command)"], Work, new Dictionary<string, string>(), TimeSpan.FromSeconds(5), CancellationToken.None);
                Require(result.ExitCode == 0);
                Require(File.ReadAllText(result.LogPrefix + ".out.log") == "a b;$(not-a-command)");
            });
            await CheckAsync("failed action runs cleanup and blocks later steps", async () =>
            {
                BuildManifest manifest = Load("""
                  <Target Name="all" Steps="fail;later">
                    <Target Name="fail"><Exec Executable="false"/></Target>
                    <Target Name="later"><Script Interpreter="sh"><Body>touch later</Body></Script></Target>
                    <Finally><Script Interpreter="sh"><Body>touch cleaned</Body></Script></Finally>
                  </Target>
                  """);
                int status = await Corsac.Build.Program.Main(["--file", manifest.File]);
                Require(status != 0 && File.Exists(Path.Combine(manifest.Root, "cleaned")) && !File.Exists(Path.Combine(manifest.Root, "later")));
            });
            await CheckAsync("validation occurs before side effects", async () =>
            {
                BuildManifest manifest = Load("""
                  <Target Name="all"><Script Interpreter="sh"><Body>touch touched</Body></Script><Unknown/></Target>
                  """);
                Require(await Corsac.Build.Program.Main(["--file", manifest.File]) != 0);
                Require(!File.Exists(Path.Combine(manifest.Root, "touched")));
            });
            await CheckAsync("timeout is reported and process is stopped", async () =>
            {
                BuildManifest manifest = Load("""
                  <Target Name="all"><Test Name="timeout" Executable="sleep" Timeout="00:00:00.15"><Argument Value="10"/></Test></Target>
                  """);
                Require(await Corsac.Build.Program.Main(["--file", manifest.File]) != 0);
                string report = Directory.GetFiles(Path.Combine(manifest.Root, "build"), "tests.xml", SearchOption.AllDirectories).Single();
                Require(XDocument.Load(report).Descendants("failure").Single().Attribute("type")!.Value == "timed-out");
            });
            await CheckAsync("independent tests report failure and success", async () =>
            {
                BuildManifest manifest = Load("""
                  <Target Name="all" DependsOnTargets="good;bad">
                    <Target Name="good"><Test Executable="true"/></Target>
                    <Target Name="bad"><Test Executable="false"/></Target>
                  </Target>
                  """);
                Require(await Corsac.Build.Program.Main(["--file", manifest.File, "--jobs", Math.Min(2, Environment.ProcessorCount).ToString()]) != 0);
                string report = Directory.GetFiles(Path.Combine(manifest.Root, "build"), "tests.xml", SearchOption.AllDirectories).Single();
                XElement suite = XDocument.Load(report).Root!;
                Require((string?)suite.Attribute("tests") == "2" && (string?)suite.Attribute("failures") == "1");
            });
        }
        Console.WriteLine($"build runner: {passed} passed, {failed} failed; fixtures: {Work}");
        return failed == 0 ? 0 : 1;
    }

    private static BuildManifest Load(string body, params string[] options)
    {
        string directory = Path.Combine(Work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "corsac.build");
        File.WriteAllText(path, "<Build FormatVersion='1' DefaultTargets='all'>" + body + "</Build>");
        return BuildManifest.Load(path, BuildOptions.Parse(options));
    }
    private static void Require(bool condition) { if (!condition) throw new Exception("assertion failed"); }
    private static void ExpectError(Action action, string text)
    {
        try { action(); }
        catch (BuildException error) when (error.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected error containing " + text);
    }
    private static void Check(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    private static async Task CheckAsync(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
}
