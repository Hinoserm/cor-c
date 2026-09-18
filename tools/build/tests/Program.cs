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
                Require(await Corsac.Build.Program.Main(["--file", manifest.File, "--jobs", "2"]) != 0);
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
