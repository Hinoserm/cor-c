# ELF tests

ELF objects, static/dynamic linking, relocations, shared libraries, and C interoperability.

`--escape` runs only the region hints and the escape engine's own tests
(RegionTests, RegionEscapeTests), which need none of the tools:

    dotnet run --project linker/tests/ElfTests.csproj -- --escape

Each of RegionEscapeTests states a few functions as their region
constraints, solves them (RegionEscape, through the linker's internals) and
checks the answers against what the program means, argued beside each test.
