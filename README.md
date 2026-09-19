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

The XML build utility and repository manifest are under active development.
The design and current implementation limits are documented in
[BUILD-SYSTEM.md](docs/BUILD-SYSTEM.md). Build the host runner with:

```sh
bash tools/bootstrap-build --list
bash tools/bootstrap-build --plan
bash tools/bootstrap-build compiler
```

Host executables use Native AOT by default on Linux x64, including the compiler,
linker, and build utility. The `bin/managed` directory identifies their Roslyn
build provenance; its executable files are native ELF binaries, not JIT launchers.
Set the standard project property `PublishAot=false` for an explicit JIT build.
The independent linker is `linker/bin/managed/Release/net10.0/corlink`. The compiler
emits ELF relocatable `.o` files with `--obj`; see
[OBJECT-FORMAT.md](docs/OBJECT-FORMAT.md). Native bootstrap activation
is not implemented yet; the runner never silently substitutes the host compiler.

.NET 10/MSBuild is permitted only to bootstrap the build utility above. Normal
Compile tasks invoke COR-C#'s own .csproj evaluator and per-file pipeline. Its
initial console-project acceptance passes, but the full compiler/self-hosting
project profile is not yet accepted. Unsupported features fail rather than
falling back to MSBuild.

With a current compiler seed available:

```sh
compiler/bin/Release/net10.0/corc project tests/integration/project/Basic.csproj
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
- `docs/`: a flat, [indexed collection](docs/README.md) of designs, guides,
  optimization plans and performance write-ups.

The planned memory-bounded project compiler and detailed ownership boundaries
are documented in [docs/REPOSITORY-LAYOUT.md](docs/REPOSITORY-LAYOUT.md).

## Status and compatibility

Any deviation from existing C# syntax or semantics, or from applicable .NET
library contracts, is considered a failure to be fixed. Platform support does
not permit replacing standard APIs with project-specific alternatives.

Separate, demand-loaded project compilation is under active development. The
current compiler can accept multiple sources together, but still performs too
much whole-program work and uses too much memory for the intended small-system
self-hosting target. Do not interpret the presence of a command or test fixture
as a claim that every requirement is complete.
