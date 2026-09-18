# Compiler

The `corc` executable: command-line/project orchestration, frontend, lowering,
IR, metadata, target-independent optimization, and target-specific compiler
support. Production C# sources live under `src/`; architecture support is under
`src/arch/`, and focused architecture tests are under `tests/arch/`. Object
format and linker implementation is owned by the repository-level `linker/`
subsystem. Requirements are in `../REQUIREMENTS.md`.
