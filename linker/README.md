# Linker

Object-level symbol resolution, relocation, static and dynamic ELF linking, and
target-independent link orchestration. Focused link tests live in `tests/`.

`linker.csproj` builds the independent `corlink` executable. Its object model
and ELF implementation do not depend on the compiler project. The compiler
references this project to emit objects and provide convenience linking.
The exchange format and managed separate-compilation gaps are documented in
`../docs/OBJECT-FORMAT.md`.
