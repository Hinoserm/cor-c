# Link-time optimization

Compact compiler summaries and conservative static-link transformations.
The linker has no frontend dependency. The first pass replaces verified direct
zero-argument i32 calls with constant results while preserving instruction size.
See docs/SEPARATE-COMPILATION.md and docs/OBJECT-FORMAT.md at repository root.
