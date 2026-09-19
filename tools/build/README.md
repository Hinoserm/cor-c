# Build utility

The installed `build` executable discovers corsac.build and executes its nested
target graph. The contract is in ../../docs/BUILD-SYSTEM.md.

Bootstrap the host runner with `bash tools/bootstrap-build --list`.
Its executable is tools/build/bin/managed/Release/net10.0/build. Add that directory to
PATH or invoke that executable directly from any directory within the repository.
It is a Native AOT host executable. This is distinct from COR-C# self-hosting,
which remains unverified for the full toolchain. Bootstrap alone uses the SDK;
normal managed compilation and AOT publishing use the owned evaluator, Roslyn,
ILC and the host C linker directly. Native AOT packs are provisioned by bootstrap.

Use `build --list`, `build --plan test`, or `build --toolchain corc`.
Normal project tasks use COR-C#'s owned evaluator. The dotnet provider is allowed
only for the build-tool component inside bootstrap. There is no fallback.
