# Managed tool execution and optimization

The compiler, linker and build utility are Release Native AOT executables by
default on Linux x64 hosts. Roslyn produces intermediate managed assemblies;
the owned build utility invokes ILC and the host linker directly, without
MSBuild. Normal invocations execute native binaries, not `dotnet run` or
`dotnet component.dll`. `bin/managed` denotes build provenance, not JIT execution.

`bash tools/bootstrap-build` provisions the initial native build utility and
matching AOT packs with the SDK, then uses the owned builder for updates and
all other projects. Requires .NET 10, a host C linker and development libraries.
Other publishing hosts currently fail explicitly; they can opt into JIT using
the standard project property `PublishAot=false`. No silent fallback is used.

CoreCLR JIT-compiles managed methods to host machine code. Tiered compilation
and dynamic PGO are enabled for managed executable projects by the owned build
utility. The repository's standard `Directory.Build.props` expresses the same
policy for SDK bootstrap builds. Explicit project exclusions are respected.
Release Roslyn compilation already uses `-optimize+`; these explicit runtime
settings preserve the .NET 10 performance defaults rather than claiming a new
speedup from previously absent JIT.

The build utility also honors standard `TieredCompilationQuickJit` and
`TieredCompilationQuickJitForLoops` properties when specified. Environment
overrides can still change runtime policy; diagnostics must account for them.
Server GC is not globally forced: many compiler worker processes each starting
multiple GC workers can increase memory use and contention.

An apphost executable is a native launcher but still uses CoreCLR and the JIT.
ReadyToRun precompiles managed code to improve startup while retaining the JIT;
Native AOT is a different deployment mode without runtime JIT. It is now
produced by the owned managed-project builder. Evaluate performance with
real short-lived per-file compilation workloads, total build time and peak
memory before making it the default. Changing a launcher filename does not
remove JIT startup cost.

## Native AOT experiment

The compiler can also be published as a host-native executable. This is an
optional SDK publishing experiment, not a new MSBuild dependency in ordinary
COR-C# project builds. It does not change generated programs' CPU baseline.
Keep its output separate from the working managed tools:

```sh
dotnet publish compiler/corc.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:IlcOptimizationPreference=Speed \
  --artifacts-path "$PWD/build/native-aot-trial" \
  -o "$PWD/build/native-aot-trial/publish/compiler"
```

Run `build/native-aot-trial/publish/compiler/corc` directly, without `dotnet`.
Set `CORC_LIB` to this repository if moving the executable elsewhere. Native
project cache signatures hash the native executable, including its embedded
linker; managed builds continue hashing their individual assemblies.

For a matching Release JIT control and reproducible compilation measurements:

```sh
dotnet build compiler/corc.csproj -c Release \
  --artifacts-path "$PWD/build/native-aot-control"
bash tests/benchmarks/native-aot-compile.sh \
  build/native-aot-control/bin/corc/release/corc.dll \
  build/native-aot-trial/publish/compiler/corc build/native-aot-results
```

The comparison alternates order, checks successful compilation, requires
byte-identical generated executables and runs both allocation benchmarks.
It records elapsed time, user/system CPU time and peak resident memory.
Competing builds on the host can distort elapsed times. This fixture does not
alone establish the speedup of a complete parallel kernel/userland build.
Native AOT is now the default worker selected by the build utility. Publishing
uses content-aware receipts covering source-generated IL, references, AOT packs,
compiler and native libraries. The executable is installed by atomic rename;
active processes retain their old image. ILC threads share the build's worker
budget. JIT exclusions retain a portable launcher and tiered-PGO settings.

### Initial Linux x64 measurements

Measured with .NET 10.0.103 / runtime 10.0.3, Release, speed-oriented AOT,
one compilation worker, three process launches per variant. Other builds were
active on the host; elapsed times are exploratory, not whole-build acceptance.

| Workload | JIT elapsed seconds | AOT elapsed seconds | JIT peak KiB | AOT peak KiB |
| --- | --- | --- | --- | --- |
| Small independent object | 0.75, 0.58, 0.68 | 0.03, 0.05, 0.04 | 51712–52992 | 23040–23296 |
| Allocation program with libraries | 23.77, 18.24, 14.05 | 21.31, 18.13, 11.11 | 231384–232568 | 118436–123288 |

The small worker's median improved from 0.68 to 0.04 seconds. Larger-program
user CPU time ranged from 16.66–23.08 seconds with JIT and 11.41–19.21 with AOT;
the changing host load prevents attributing a precise whole-build speedup.
Both object files and both Linux executables were byte-identical. Both generated
executables passed all nine allocation checks. An AOT-compiled `.csproj` smoke
program ran successfully and its second project build reused every object.
The AOT compiler publish completed without warnings after the cache identity fix.

Next acceptance is routing a complete parallel kernel/userland build through
native workers, including subprocess LTO and rebuild invalidation. Do not
replace an active managed toolchain merely because this microbenchmark passes.

Do not rebuild or replace tool assemblies underneath an active production
build. Publish changes, finish the active invocation, then rebuild and validate
the tool generation as a coherent set.
