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

Do not rebuild or replace tool assemblies underneath an active production
build. Publish changes, finish the active invocation, then rebuild and validate
the tool generation as a coherent set.
