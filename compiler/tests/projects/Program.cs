using Corsac.Projects;

namespace Corsac.Tests.Projects;

public static class Program
{
    public static int Main()
    {
        string work = Path.Combine(Path.GetTempPath(), "corc-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action operation)
        {
            try { operation(); } catch (InvalidDataException) { checks++; return; }
            throw new Exception("Unsupported or invalid project was accepted");
        }
        try
        {
            string path = Path.Combine(work, "app.csproj");
            File.WriteAllText(Path.Combine(work, "Program.cs"), "class Program { static int Main() => 0; }");
            File.WriteAllText(Path.Combine(work, "Removed.cs"), "class Removed { }");
            Directory.CreateDirectory(Path.Combine(work, "obj"));
            File.WriteAllText(Path.Combine(work, "obj", "Generated.cs"), "class Ignored { }");
            File.WriteAllText(Path.Combine(work, "Directory.Build.props"), "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>SHARED</DefineConstants></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(work, "extra.props"), "<Project><PropertyGroup><AssemblyName>Imported</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="extra.props" Condition="Exists('extra.props')" />
                  <PropertyGroup><OutputType>Exe</OutputType><Configuration>Wrong</Configuration></PropertyGroup>
                  <Choose>
                    <When Condition="'$(Configuration)' == 'Release'"><PropertyGroup><DefineConstants>$(DefineConstants);RELEASE</DefineConstants></PropertyGroup></When>
                    <Otherwise><PropertyGroup><DefineConstants>OTHER</DefineConstants></PropertyGroup></Otherwise>
                  </Choose>
                  <ItemGroup><Compile Remove="Removed.cs" /><Compile Update="Program.cs"><Link>Main.cs</Link></Compile></ItemGroup>
                  <Unsupported Condition="false" />
                </Project>
                """);
            EvaluatedProject project = ProjectEvaluator.Evaluate(path, "Release", null);
            Check(project.AssemblyName == "Imported", "Import order");
            Check(project.Sources.Length == 1 && project.Sources[0].EndsWith("Program.cs"), "SDK glob, Remove and default obj exclusion");
            Check(project.Defines.Contains("SHARED") && project.Defines.Contains("RELEASE"), "Global configuration and Choose");
            Check(project.Defines.Contains("NET") && project.Defines.Contains("NET10_0"), "Implicit framework defines");
            Check(ProjectCondition.Evaluate("('Release' != 'Debug') And !false", work), "Boolean conditions");
            Check(ProjectCondition.Evaluate("'10.0' >= '9.0' Or false", work), "Numeric condition comparison");
            Check(!ProjectCondition.Evaluate("Exists('absent')", work), "Exists condition");
            // The runtime settings a program is built with (--runtime-knob):
            // each value what the SDK's runtimeconfig.json gives the program.
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><Limit>33554432</Limit></PropertyGroup>
                  <ItemGroup>
                    <RuntimeHostConfigurationOption Include="System.GC.HeapHardLimit" Value="$(Limit)" />
                    <RuntimeHostConfigurationOption Include="Knob.On" Value=" True " />
                    <RuntimeHostConfigurationOption Include="Knob.Number" Value="007" />
                    <RuntimeHostConfigurationOption Include="Knob.Hex" Value="0x10" />
                    <RuntimeHostConfigurationOption Include="Knob.Twice;Knob.Updated;Knob.Gone;Knob.Kept" Value="first" />
                    <RuntimeHostConfigurationOption Include="Knob.Twice"><Value>one</Value><Value>second</Value></RuntimeHostConfigurationOption>
                    <RuntimeHostConfigurationOption Update="knob.updated" Value="after" />
                    <RuntimeHostConfigurationOption Update="Knob.Kept" />
                    <RuntimeHostConfigurationOption Remove="KNOB.GONE" />
                  </ItemGroup>
                </Project>
                """);
            project = ProjectEvaluator.Evaluate(path, "Release", null);
            Check(project.RuntimeKnobs.SequenceEqual(new[] { "System.GC.HeapHardLimit=33554432", "Knob.On=true", "Knob.Number=7", "Knob.Hex=0x10",
                "Knob.Updated=after", "Knob.Kept=first", "Knob.Twice=second" }), "Runtime settings as runtimeconfig.json has them: " + string.Join(" ", project.RuntimeKnobs));
            File.WriteAllText(path, "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><Compile Include='Program.cs'/></ItemGroup></Project>");
            Reject(() => ProjectEvaluator.Evaluate(path, "Release", null));
            File.WriteAllText(path, "<Project Sdk='Microsoft.NET.Sdk'><Unknown/></Project>");
            Reject(() => ProjectEvaluator.Evaluate(path, "Release", null));
            File.WriteAllText(path, "<Project Sdk='Microsoft.NET.Sdk'><Import Project='app.csproj'/></Project>");
            Reject(() => ProjectEvaluator.Evaluate(path, "Release", null));
            File.WriteAllText(path, "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><ProjectReference Include='app.csproj'/></ItemGroup></Project>");
            Reject(() => ProjectGraph.Evaluate(path, "Release", null));
            DependencyTests.Run(work);
            Console.WriteLine("project evaluation: " + checks + " checks passed without MSBuild");
            return 0;
        }
        finally { Directory.Delete(work, recursive: true); }
    }
}
