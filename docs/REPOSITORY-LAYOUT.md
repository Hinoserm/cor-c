# COR-C# repository layout and extraction plan

## Decision status

The repository is `Hinoserm/cor-c`, with a checkout at `~/projects/cor-c`.
The user has approved public visibility: the project should be available for
others to inspect and experiment with. Public documentation must distinguish
working features from experimental, incomplete, and unverified requirements.

The repository now has separate compiler, linker, runtime,
standard-library, tests, examples, and documentation directories. Compiler
implementation is under `compiler/src`; targeted tests live with their owning
systems and broad tests remain at the repository root. Every maintained
directory has a README. Runtime and standard-library platform boundaries below
remain the design contract as the source split continues.

This document records the chosen structure and migration contract. The public
repository has been created and the initial source extraction is complete;
separate project compilation, full source-file splitting, and CORSAC/OS consumer
cleanup remain implementation work.

## Repository tree

```text
cor-c/
  README.md
  LICENSE
  REQUIREMENTS.md
  TODO.md
  compiler/
    README.md
    corc.csproj          the one executable: build, compile, link, asm, index
    src/
      arch/
        corsac/
          assembler/
        x86/
          backend/
      driver/
      frontend/
        syntax/
        symbols/
      ir/
      optimizations/
      metadata/
    tests/
      arch/
        corsac/
        x86/
      optimizations/
  linker/                library reached as `corc link`
    README.md
    linker.csproj
    src/
      driver/
      elf/
      model/
    tests/
  runtime/
    README.md
    src/
      core/
      arch/
        x86/
      platforms/
        linux/
        corsac/
  stdlib/
    README.md
    src/
      System/
        Collections/
        IO/
        Net/
        Security/
          Cryptography/
        Text/
        Threading/
      platforms/
        linux/
        corsac/
  tests/
    README.md
    language/
    integration/
    benchmarks/
  tools/
    build/
      build.csproj
      src/
      tests/
  examples/
    README.md
  docs/
    README.md
    BUILD-SYSTEM.md
    REPOSITORY-LAYOUT.md
    OBJECT-FORMAT.md
    X86-BACKEND.md
    DOTNET-LIBRARY.md
    SELFHOST.md
    LANGUAGE-TESTS.md
    BENCHMARKS.md
    OPTIMIZATIONS.md
    PASS-INVENTORY.md
    LARGE-BATCH.md
```

Every actual directory receives a README, including `src/`, intermediate
directories, and platform/arch directories; repeated README entries are
omitted from the diagram for readability. Create a directory when it has actual
content or is an explicitly requested root, not merely for a hypothetical port.
No Windows, ARM, or other unimplemented port is implied by this tree.

## Compiler boundary

`compiler/` contains the compiler executable, project orchestration, frontend,
lowering, target-independent IR, optimization, metadata, and target-specific
compiler support below `compiler/src/arch/`. Its architecture-focused tests are
under `compiler/tests/arch/`. `linker/` contains object-format and linking
implementation together with linker-focused tests. Runtime and library
implementations do not belong in either subsystem merely because generated
programs or self-hosting need them.

Architecture-independent syntax, binding, IR, analyses, and optimizations remain
separate from instruction selection, registers, encoding, and machine peepholes.
ELF is a file format, not a synonym for Linux or x86. Target configuration selects
the architecture, ABI, object format, and platform; it must not hard-code that
every x86 program is a Linux program.

Use one top-level class/type per matching source file where practical, retaining
purposeful partial-class subdivisions. The syntax and symbol models, IR model,
x86 machine IR, and ELF records have been split this way. Do not confuse moving
files with independent compilation or bounded compiler memory use.

`corc` is the only executable. The linker and the build utility are library
projects it references, reached as `corc link` and `corc build`, so one
process holds the build graph, the compiler and the linker and schedules
their work against one pool of threads. The separation that matters is still
enforced by the project references: the linker owns the relocatable object
model and has no dependency on compiler frontend code, and it must not
reference the compiler. The root corsac.build describes the components; see
[BUILD-SYSTEM.md](BUILD-SYSTEM.md).

## Runtime layout

### `runtime/src/core`

The execution machinery required by compiled managed code: allocation and GC
algorithms, exception machinery, runtime type support, compiler helper routines,
and architecture-neutral thread/safepoint bookkeeping. These files must not
contain a particular operating system's syscall numbers or hard-coded x86
instructions. Memory layout and ABI facts must come through explicit contracts.

### `runtime/src/arch/x86`

Machine-dependent execution mechanisms: register capture, stack/frame operations,
machine stubs, and architectural portions of TLS or exception transitions.
An x86 mechanism that does not depend on an OS belongs here.

### `runtime/src/platforms/<platform>`

The low-level services connecting the execution engine to its environment:
virtual-memory acquisition/release, native threads, blocking/waking, clocks,
process exit, and low-level host-service access used by library adapters.

OS-and-architecture-specific code belongs below the combined location, such as
`platforms/linux/arch/x86` for Linux i386 syscall conventions. Do not put Linux
syscall numbers in generic `arch/x86`, or duplicate OS-neutral x86 machinery in
every platform directory.

`corsac` denotes the CORSAC user-process environment. Bare x86 execution
mechanisms without user-process OS services belong below `runtime/src/arch/x86`;
they are not a separate top-level layout category. CORSAC device drivers, the
kernel, and bootloader product code remain in `corsac86`; only reusable execution
support and a defined host interface belong in this repository. Mixed current
files must be split by responsibility.

## Standard-library layout

### `stdlib/src/System`

Organize the public C#/.NET-compatible surface by namespace. Algorithms and
public behavior shared by all targets live here: collections, text processing,
portable cryptography, and common implementations of I/O, networking, threading,
and other library APIs. Namespace directories are organizational, not an excuse
to change public names or contracts.

Public exception, threading, and type APIs belong to the standard-library
surface even where their implementation delegates to runtime machinery. Their
low-level execution helpers belong in the runtime. Avoid two implementations of
the same public contract or a runtime dependency on high-level library services
that themselves require the runtime to initialize first.

### `stdlib/src/platforms/<platform>`

Internal adapters translate host services into standard-library semantics:
file/path behavior, native errors and public exceptions, process/environment
operations, console handling, sockets, and platform-specific integrations.
Application code still uses the ordinary C#/.NET APIs. The directory name must
not leak out as a replacement public API such as a Linux-only substitute for
`System.IO.File`.

`platforms/posix` is shared implementation only where behavior is genuinely
common to the supported POSIX targets. Linux and CORSAC adapters specialize
actual differences; a POSIX label must not assume identical syscalls or layouts.
Raw calling conventions stay in runtime platform/architecture support.

A bare x86 image includes only support that can be implemented in that
environment. Where the standard permits unsupported-platform behavior, report
it through the standard contract (for example, `PlatformNotSupportedException`),
not silent success or fabricated results. Missing required behavior remains an
explicit compatibility defect, not a completed alternative implementation.

## Target selection and dependencies

- Builds explicitly select a compatible architecture, platform, ABI, and file
  format. Select source sets through project/target metadata; do not compile
  every platform implementation and hope unused pieces disappear.
- Share common code through small, defined internal contracts. Runtime helpers
  and public standard-library APIs are different layers.
- Keep compiler-required ABI facts versioned and checked across artifacts;
  avoid contradictory copies of calling conventions or object-layout constants.
- Do not use this move to add functionality or alter ABI silently. First preserve
  behavior with matching artifacts, then make separately reviewed changes.
- Preserve default 486+x87 support and the requirements in
  `REQUIREMENTS.md`, including standards compatibility, demand-loaded
  declarations, bounded memory, and native self-hosting.

## Tests, documentation, and migration

Keep focused subsystem tests alongside their owners. Broad language,
integration and benchmark fixtures live in the top-level `tests/` tree,
including runtime and standard-library tests needed by this toolchain. Keep
kernel/driver/boot acceptance in the OS repository, with explicit integration
coverage at the boundary. Do not copy large generated results or private machine
images into Git.

Move compiler-specific documentation to `docs/`; split mixed compiler/OS
documents so ownership is clear and the OS repository retains its requirements.
Keep docs/ flat and index it with docs/README.md. Optimization plans, pass
inventories and measured performance write-ups are documentation and belong
there, not beneath tests/. Short local READMEs explain directory purpose and
build/test entry points, linking to the detailed guides. Root REQUIREMENTS.md
and TODO.md keep their existing responsibilities.

Migration sequence:

1. Agree on the detailed layout (public repository visibility is approved).
2. Inventory source, tests, documents, scripts, and cross-repository dependencies.
3. Extract relevant history where practical, excluding unrelated OS material,
   private conversations, credentials, build outputs, and temporary artifacts.
4. Move files and repair all build/test/source inventories without algorithmic
   changes mixed into the initial migration.
5. Verify a standalone checkout can build and run representative tests without
   undeclared reads from `corsac86`. Record pre-existing failures explicitly.
6. Create/push the new repository and establish the agreed OS dependency on it
   before removing the recoverable original compiler copy.

The runtime/stdlib subdivisions are approved. CORSAC86 will obtain pinned
toolchain inputs through the build utility; source-lock and dependency-fetch
implementation remains tracked in the root TODO.md.
