# Build utility

The installed `build` executable discovers corsac.build and executes its nested
target graph. The contract is in ../../docs/BUILD-SYSTEM.md.

Bootstrap the host runner with `dotnet build tools/build/build.csproj -c Release`.
Its executable is tools/build/bin/Release/net10.0/build. Add that directory to
PATH or invoke that executable directly from any directory within the repository.
It is a host .NET runner until native self-hosting support is verified.

Use `build --list`, `build --plan test`, or `build --toolchain corc`.
Normal project tasks use COR-C#'s owned evaluator. The dotnet provider is allowed
only for the build-tool component inside bootstrap. There is no fallback.
