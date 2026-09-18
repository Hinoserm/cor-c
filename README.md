# CORSAC/C#

CORSAC/C# (COR-C# for short) is a native C# compiler, runtime, standard
library, assembler, and linker. Its first mature backend emits 32-bit x86 code
for 486-class processors with x87 floating point. Linux i386 executables run
directly, and CORSAC/OS consumes the same compiler and portable libraries.

The compiler already parses and binds a substantial C# surface, performs
whole-program optimization, emits ELF objects and executables, supports shared
libraries, and can build a native copy of itself far enough to compile and run
smoke programs. It is usable for experimentation, but it is not yet a complete
C# or .NET implementation. Known incompatibilities are defects and are tracked
in [REQUIREMENTS.md](REQUIREMENTS.md).

## Build

.NET 10 is required for the bootstrap compiler:

```sh
dotnet build compiler/corc.csproj -c Release
compiler/bin/Release/net10.0/corc compile examples/hello/Program.cor -o hello
./hello
```

Run the host-side unit programs and language tests with:

```sh
bash tests/run-unit.sh
bash tests/language/run.sh
```

The language suite produces Linux i386 executables, so its host must support
running them. See the READMEs under `tests/` for narrower gates.

## Repository map

- `compiler/`: compiler executable, target-independent frontend/IR/optimizer,
  lowering, and metadata.
- `linker/`: object formats, relocations, static/dynamic linking, and focused
  linker tests.
- `compiler/src/arch/`: architecture-specific compiler support, including
  assemblers, ABIs, code generation, and encoders. Its focused tests live
  under `compiler/tests/arch/`.
- `runtime/`: managed execution support plus architecture and host adapters.
- `stdlib/`: C#/.NET-compatible public library implementation.
- `tests/`: broad language, integration, and benchmark coverage.
- `examples/`: small programs intended for readers and experiments.
- `docs/`: design and development documentation.

The planned memory-bounded project compiler and detailed ownership boundaries
are documented in [docs/development/REPOSITORY-LAYOUT.md](docs/development/REPOSITORY-LAYOUT.md).

## Status and compatibility

Any deviation from existing C# syntax or semantics, or from applicable .NET
library contracts, is considered a failure to be fixed. Platform support does
not permit replacing standard APIs with project-specific alternatives.

Separate, demand-loaded project compilation is under active development. The
current compiler can accept multiple sources together, but still performs too
much whole-program work and uses too much memory for the intended small-system
self-hosting target. Do not interpret the presence of a command or test fixture
as a claim that every requirement is complete.

## License

MIT. See [LICENSE](LICENSE).
