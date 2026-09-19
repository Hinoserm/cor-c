# Managed tool execution and optimization

The .NET-hosted compiler, linker and build utility are built as Release managed
assemblies. Normal invocations execute the already-built assembly with
`dotnet component.dll`; they do not use `dotnet run`. The portable executable
name is currently a small launcher for that assembly.

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
Native AOT is a different deployment mode without runtime JIT. Neither is
currently produced by the owned managed-project builder. Evaluate either with
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
Native AOT is not yet the default worker selected by the build utility.

Do not rebuild or replace tool assemblies underneath an active production
build. Publish changes, finish the active invocation, then rebuild and validate
the tool generation as a coherent set.
