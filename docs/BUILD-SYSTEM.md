# CORSAC build system

## Hosted managed components without MSBuild

`<Compile Project="..." Toolchain="managed" />` uses the same owned SDK-project
evaluator as the native compiler, then invokes the installed SDK's C# compiler
directly. It does not invoke MSBuild. This permits host build utilities to use
standard C# and .NET before native self-hosting is complete. `corc` remains the
native code-generation route; `dotnet` remains the MSBuild bootstrap-only route.
Managed outputs are under `bin/managed/<Configuration>/<TargetFramework>/`.
Project references are built first; framework references, conditional symbols,
implicit global usings, startup class, nullable, unsafe, overflow and optimization
settings are passed to the managed compiler. Unsupported packages/items still
fail explicitly. Native project support is not implied by managed acceptance.

## Contract and implementation status

`build` is an installed executable which discovers `corsac.build` from the
current directory. It manages repository dependencies, nested targets,
compilation, tests, installation trees, and images. It must ultimately run as
a native CORSAC/C# application on Linux and CORSAC, including small x86 hosts.
This document specifies the intended system. The implementation milestones in
TODO.md distinguish available features from requirements; a declaration here
does not imply that its executor is implemented.

The first implementation checkpoint provides a host .NET runner: discovery,
directory defaults, nested targets, prerequisites/ordered steps, cycle checks,
simple properties, Compile through the explicit dotnet provider, Exec, Script,
Test, Message, Error and Finally. It shares a logical-CPU worker budget across
subprocesses and cooperating tools, supports timestamp-based declared file
tasks, retains stdout/stderr logs and writes JUnit test reports. Imports,
profiles, native project evaluation, source fetching/locks, artifact references,
memory/resource estimates and bootstrap activation remain implementation work.
The repository's bootstrap/native target deliberately fails with this status;
building a host seed must never falsely activate a native toolchain.

There is no solution file or parallel build.conf. Component sources, project
references, resources, and language options have one authority: standard
SDK-style .csproj files and their standard imports. The orchestration file
references projects; it must not duplicate their compile inventories.

## Invocation and discovery

Commands have the form `build [target-path] [options]`. The runner searches the
current directory and then its parents for the nearest corsac.build. An explicit
`--file path` overrides discovery. With no target, the longest matching
Directory default selects a target; otherwise Build.DefaultTargets applies.
Discovery never searches dependency checkouts implicitly.

Examples:

```text
build bootstrap
build
build compiler
build test/unit/linker
build footjuice --profile linux-486 --configuration Release
build --list
build --plan test
build --file examples/other/corsac.build test
```

`bootstrap`, `test`, and `footjuice` are manifest targets, not reserved verbs.
CLI controls are options: --list, --plan, --file, --jobs, --configuration,
--profile, --toolchain, --offline, --update-lock, and --property Name=Value.
Unknown options and unresolved target paths fail before actions start.

## XML envelope and naming

```xml
<Build FormatVersion="1" DefaultTargets="all">
  <PropertyGroup>
    <Configuration>Release</Configuration>
  </PropertyGroup>
  <Components>
    <Project Name="compiler" Path="compiler/corc.csproj" />
  </Components>
  <Directory Path="compiler" DefaultTargets="compiler" />
  <Target Name="all" DependsOnTargets="compiler" />
  <Target Name="compiler">
    <Compile Project="compiler" Toolchain="active" />
  </Target>
</Build>
```

Names are case-sensitive identifiers containing letters, digits, underscores,
dashes, and dots. Slash separates target scopes. Duplicate sibling names,
duplicate component names, unknown elements/attributes, unsupported format
versions, and ambiguous definitions are errors with file and line diagnostics.
XML DTDs and external entities are disabled. Text is allowed only in documented
text-bearing elements. XML comments do not affect execution.

Paths are relative to their declaring file unless documented otherwise. Imported
files preserve their base directory. Absolute paths are permitted where a path
is expected. Tool arguments are not paths unless that task specifies otherwise.

## Nested targets, groups, and references

```xml
<Target Name="bootstrap" Steps="seed;native;verify;activate">
  <Target Name="seed"><Compile Project="compiler" Toolchain="dotnet" /></Target>
  <Target Name="native" DependsOnTargets="seed">
    <Compile Project="compiler" ToolchainFrom="seed" />
  </Target>
  <Target Name="verify" DependsOnTargets="native">
    <Test Executable="$(NativeCompiler)" Name="native-help">
      <Argument Value="--help" />
    </Test>
  </Target>
  <Target Name="activate" DependsOnTargets="verify">
    <ActivateToolchain From="native" />
  </Target>
</Target>
<Target Name="footjuice" Steps="/bootstrap;/all;/test" />
```

Each target may contain child Target definitions, prerequisite references,
ordered step references, ordinary tasks, and an optional Finally block.
Definitions do not execute merely because their parent executes.

References resolve from the referencing target: its children, then siblings,
then successive enclosing scopes. The first exact match wins. `/name/path`
resolves from the manifest root. `dependency::/name/path` resolves in a declared
dependency workspace. Resolution never searches arbitrary descendants. The
current target is not an implicit match for its own name, but an explicit
self-reference is a cycle error.

DependsOnTargets is a semicolon-separated set of prerequisites; their list
order does not impose execution order. Independent prerequisites may run in
parallel. Steps is a semicolon-separated sequence: each referenced target
completes before the next starts. After prerequisites and Steps, task elements
execute in document order. A target with only prerequisites is a parallel group;
a target with only Steps is an ordered group. A target with only nested
definitions is invalid: it must explicitly select work or be documented empty
with AllowEmpty="true".

Finally contains tasks and runs after attempted execution, including failure
and cooperative cancellation. Original errors are retained when cleanup also
fails. A validation failure does not run cleanup or any other action. Cleanup
has an independent bounded timeout. Abrupt host termination cannot guarantee
cleanup; resumable actions must tolerate abandoned work directories.

The entire selected graph is validated before executing anything, including
steps, prerequisites, artifact edges, and project dependency cycles. Execution
deduplicates target instances by canonical scope, profile, global properties,
and resolved toolchain identity. Ordered invocation after toolchain activation
is evaluated with the newly published identity, never a mutable compiler path
captured by an already running operation. Plan output must show these barriers.

## Properties and profiles

PropertyGroup and $(Name) expansion follow the supported MSBuild conventions.
Global CLI properties are immutable and override manifest defaults. Reserved
properties include WorkspaceDirectory, ManifestDirectory, Configuration,
Profile, OutputDirectory, and Jobs. Native platform, architecture, CPU, ABI,
and object format are independent from the .NET TargetFramework contract.
Profiles supply groups of property values; local task inputs may specialize
them. Undefined orchestration properties are diagnosed rather than silently
becoming malformed command arguments. This stricter orchestration rule does
not change MSBuild's property semantics inside .csproj evaluation.

The bootstrap profile is the build host, regardless of the product profile.
Host tools and target artifacts must never share cache identities. Named
profiles and full Condition expressions remain separately tracked features
until implemented; unsupported declarations fail, not silently disappear.

## Process, script, and test tasks

```xml
<Exec Executable="objdump" WorkingDirectory="$(WorkspaceDirectory)">
  <Argument Value="-d" />
  <Argument Value="$(OutputDirectory)/hello" />
  <Environment Name="LC_ALL" Value="C" />
</Exec>
<Script Interpreter="sh" File="scripts/prepare.sh" Timeout="00:01:00">
  <Argument Value="$(OutputDirectory)" />
</Script>
<Script Interpreter="sh">
  <Body><![CDATA[printf '%s\n' "$OUTPUT_DIR"]]></Body>
  <Environment Name="OUTPUT_DIR" Value="$(OutputDirectory)" />
</Script>
<Test Name="linker" Executable="dotnet" Timeout="00:02:00" ExpectedExitCode="0">
  <Argument Value="run" />
  <Argument Value="--project" />
  <Argument Value="linker/tests/ElfTests.csproj" />
</Test>
```

Arguments are distinct argv values, with no implicit shell parsing. Script
requires an interpreter and exactly one File or Body. Inline bodies are
materialized as temporary files; no quoting-based injection into a command line.
An interpreter is an external dependency and must exist on the executing host.
Standard C# helper programs are ordinary projects compiled and invoked via
artifact references. No second inline C# dialect is introduced.

Exec, Script, and Test support WorkingDirectory, Environment children, Timeout,
and ExpectedExitCode. Failures identify the target, task, executable, exit
status, elapsed time, and log location. Output streams drain concurrently and
are retained separately on disk; memory must not grow with process output.
Cancellation and timeout terminate the owned process tree and await its exit.

Test additionally records a named result. States are passed, failed, timed out,
skipped, and infrastructure error. Test group failures do not prevent already
scheduled independent tests from completing. The build exits nonzero if any
selected test fails. Reports include a human summary and JUnit XML; stdout and
stderr are referenced as log files rather than retained as unbounded strings.
Adapters may import framework results without counting one process as all of
its individual cases. Structured import is a distinct feature milestone.

Explicit test invocations always run tests. Only prerequisite compilation is
incremental by default. Test caching requires an explicit policy. Arbitrary
commands and scripts also run each time unless they declare a complete cache
contract, including tool identity, inputs, outputs, properties and environment.

## Compilation and .csproj compatibility

Compile references a component name or a .csproj path, configuration, and
toolchain. The operational evaluator is owned by COR-C# and implements standard
.csproj semantics directly. Neither evaluation nor normal compilation invokes
MSBuild. MSBuild is allowed only to bootstrap the build system; the existing
host dotnet adapter is transitional bootstrap machinery, not an acceptable
normal project provider. Unsupported active features fail explicitly rather
than falling back to MSBuild or the host compiler.

Initial native acceptance requires SDK-style console/library projects with:

- PropertyGroup, ItemGroup, imports and import order, conditions, Choose,
  property/item expansion, metadata, transforms, and SDK default source items;
- Include/Exclude/Remove/Update, default bin/obj exclusions, Directory.Build.props
  and Directory.Build.targets, and command-line global properties;
- ProjectReference and relevant metadata, framework selection and multiple
  configurations, reference assemblies, generated global usings/assembly info;
- language version, nullable, unsafe, checked arithmetic, defines, warnings,
  entry points, resources and relevant output properties;
- target ordering, before/after hooks, incremental inputs/outputs, and the
  built-in tasks required by the supported SDK build path.

Compare native evaluation and execution with Microsoft's evaluated properties,
items, target ordering and project graphs. Do not flatten XML or silently ignore
unknown active imports/tasks. A .NET target framework represents a real API
contract; unsupported language/library behavior is a compatibility defect.

NuGet restore is a later acceptance milestone: retain standard PackageReference,
NuGet configuration and packages.lock.json. Resolve transitive dependencies,
framework assets, build imports and generators using standard behavior. Native
use of binary packages requires compatible managed metadata/IL implementation;
downloading a DLL is not sufficient. Custom task assemblies, arbitrary property
functions, Roslyn generators/analyzers and additional SDKs remain explicit gaps
until supported. Unknown inactive branch content follows MSBuild evaluation
rules rather than an unconditional rejection of every XML node.

## Bootstrap and toolchain publication

### Owned project pipeline checkpoint

`corc project component.csproj --configuration Release --jobs N` evaluates the
supported SDK-style profile without launching MSBuild. Properties, conditions,
imports, directory build props/targets, source globs, Include/Exclude/Remove/
Update, and source project-reference traversal are implemented. Unsupported
features fail explicitly; full profile acceptance is not claimed. Generated
global usings, metadata transforms, custom targets/tasks, standalone library
packaging and complete project-reference metadata remain outstanding.

The coordinator indexes declarations, compiles source files one at a time in
one compiler process, and uses the worker budget inside each unit. Cached units
record source/tool/options identities and consumed declaration fingerprints.
Name and extension lookup results, including misses, are recorded so adding a
previously absent candidate invalidates consumers. Ordinary provider body edits
do not invalidate callers; generic bodies use a conservative source fingerprint.
Source-generation changes during compilation prevent final publication.

The runtime provider still compiles the library source set together, and unit
binding still uses library declaration sources. Replacing that eager library
handling is the next requested stage. Bounded project-body residency does not
establish a total process RSS ceiling.

The Basic.csproj fixture passed native execution, unchanged output timestamps
with zero rebuilt units, and a body-only edit with one of two units rebuilt.
Evidence: build/native-project-pipeline.log and build/project-pipeline.Q0o4aB/.
Evaluator and build-runner host checks were compiled directly with the C#
compiler for diagnostics; they did not invoke MSBuild.

### Activation requirements

`build bootstrap` is a manifest-defined chain: host seed, native compiler/build
utility, native rebuild, verification, and atomic activation. Versions, source
identities, target host and artifact hashes are recorded. A failed verification
must leave the previous active toolchain intact. A running toolchain is immutable;
new output is built beside it. On Windows publication must also respect locked
executables. The first installed runner may be a host .NET build; this does not
count as a self-hosted runner.

`build` uses the active toolchain unless an explicit --toolchain override is
supplied. No active toolchain is an actionable error directing the operator to
bootstrap. A dotnet-built compiler, a native compiler which compiles hello, and
a compiler which rebuilds itself are distinct acceptance milestones.

## Fetching, locks, artifacts and installation

Source declarations identify Git or archive dependencies. corsac.build.lock
records exact commits, archive hashes, patch hashes and relevant tool identities.
Normal builds fetch only missing locked inputs; --update-lock explicitly changes
resolution. --offline requires all inputs locally. NuGet's existing lock files
are retained rather than copied into this lock. Downloads are verified before
use; archive extraction must reject traversal and escaping links. Checkouts are
owned work areas and never overwrite an unrelated user's dirty checkout.

Tasks publish named artifacts. A From/ToolchainFrom/artifact reference adds a
dependency, including when a nested target is invoked directly. Referencing a
missing output is an error. Output is staged privately and published only after
success. Installation normally assembles a destination tree; installing onto
the host is an explicit target with an explicit destination. Disk image tools
are host executables consuming target artifacts. Image recipes declare size,
geometry, partition/filesystem layout and boot settings as inputs.

Build work belongs under build/, durable download caches under a configured
cache directory, and per-project intermediates under isolated obj directories.
SDK and native artifacts never reuse each other's intermediate directories.
Cleaning is restricted to recorded owned outputs; no broad directory deletion
based on an unchecked property or downloaded manifest is permitted.

## Scheduling and resource limits

The default CPU budget is Environment.ProcessorCount: the logical processors
available to this process. `--jobs N` can lower it. Independent dependency
branches run concurrently; Steps and tasks within a target remain ordered.
The host Compile adapter leases currently idle worker slots and supplies that
count to MSBuild and DOTNET_PROCESSOR_COUNT. Leases share one global semaphore
and are returned on success, failure and cancellation; nested compilers do not
each receive the full machine count.

Exec, Script and Test use one slot by default. `Workers="auto"` allows a
cooperating task to lease the remaining idle slots. Every child receives its
lease as CORSAC_BUILD_JOBS and DOTNET_PROCESSOR_COUNT. Scripts must pass that
budget to native tools, e.g. `corc --jobs` or `make -j`; arbitrary scripts which
ignore it cannot be forcibly constrained by a process semaphore. The repository
library-build script passes it to the compiler. Memory estimates and named
resource locks below remain future scheduling work.

## Incremental execution

Compile uses COR-C#'s owned standard-project evaluator and incremental records:
project references, supported SDK defaults, source items, compiler options and
outputs remain its responsibility. MSBuild is not the operational engine.
The build runner must not guess a project's dependencies by scanning only .cs
files or skip evaluation just because its main executable already exists.

File-producing Exec and Script tasks may declare semicolon-separated `Inputs`
and `Outputs`, both required together. Paths are explicit files relative to the
manifest directory, with property expansion; glob declarations are not yet
implemented and fail explicitly. Script files and resolvable tool executables
are added as inputs. Tool payloads behind wrappers must be declared explicitly.

```xml
<Exec Executable="$(Compiler)"
      Inputs="program.cor;$(Compiler);compiler/bin/Release/net10.0/corc.dll"
      Outputs="build/program">
  <Argument Value="compile" />
  <Argument Value="program.cor" />
  <Argument Value="-o" /><Argument Value="build/program" />
</Exec>
```

A successful task records input/output paths, byte lengths and UTC modification
times under build/state. It skips only when every output exists, no input is
newer than the oldest output, the expanded task/options match, and the recorded
file state still matches. This also detects timestamp rollback and externally
changed outputs. Missing required inputs fail. Missing outputs rerun. Failed or
cancelled tasks never publish successful state; changes to inputs during a run
invalidate publication. Timestamp/size checking is not content hashing: edits
which preserve both cannot be detected by this initial mode.

Tests and undeclared arbitrary commands always run. This avoids treating an
interactive action, test result or side effect as a cacheable compiled artifact.
Successful source and output timestamps need not be equal; literal inequality
would rebuild ordinary successful compilations forever.

### Remaining resource and artifact work

The graph runner coordinates CPU workers, estimated resident memory, and named
exclusive resources across tasks. A dependency wait must not hold a process
slot. External tools receive the remaining worker budget; compiler jobs and
Make jobs cannot independently multiply the global limit. Native low-memory
operation supports one worker and bounded project/metadata residency.

Task results and logs are stable in naming, even when execution order differs.
Artifact cache keys include evaluated project inputs, imports, source identities,
compiler/runtime identities, target parameters and declared environment.
Concurrent readers never consume partially published outputs.

## Imports and ownership

An Import mounts a fragment in the declaring scope; nested targets stay nested.
Recursive imports, duplicate definitions and inconsistent format versions are
errors. ProjectReference remains authoritative for C# component dependencies.
Dependency-qualified targets explicitly cross workspace boundaries; imports
never accidentally change the workspace root. XML namespaces and task-extension
loading require a future versioned contract and cannot be silently accepted.

The implementation lives in tools/build/src, one type per file, with focused
tests in tools/build/tests. Tests spanning toolchain components stay under root
tests. No .sln is required. Repository changes to the build design update this
document and implementation status together.

## Standards references

- [MSBuild SDK imports](https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-use-project-sdk)
- [MSBuild evaluation and execution](https://learn.microsoft.com/en-us/visualstudio/msbuild/build-process-overview)
- [Directory build customization](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- [.NET reference assemblies](https://learn.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies)
- [NuGet PackageReference](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
