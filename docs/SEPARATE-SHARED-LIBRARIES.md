# Separately compiled shared libraries

`corc compile --shared --obj` emits a position-independent relocatable object
instead of directly linking a shared library. Compile each owned source as a
separate process using the assembly declaration index and dependency receipts.
Pass already-built dependencies with `--link-shared` at both compile and link
time, preserving canonical definitions supplied by those dependencies.

Exactly one source object per library supplies the image initializer. Other
objects use `--no-shared-init`; their managed metadata still participates in
the final image directory. This initializer registers the whole linked image,
not just the source file that supplied it.

Link the objects with `corlink --shared ... -o libName.so`. The output filename
sets its SONAME. Needed shared libraries are selected from the offered
`--link-shared` inputs. Shared output cannot use flat or fixed-address options.
PIC objects currently retain machine code rather than regeneratable LTO IR.
`--no-undefined` requires every dynamic import to be supplied by the offered
libraries before the output is written. The OS library build enables this to
reject missing dependencies and dependency cycles without external scripts.

The CORSAC image builder uses this path for the runtime and class libraries.
Its shared process budget bounds concurrent source compilers. Libraries remain
dependency-ordered; single-source libraries do not gain file-level concurrency.
The focused regression is `tests/integration/separate-dynamic.sh`, which links
two independently compiled PIC objects into a library and executes a consumer.

To run the shared-runtime acceptance suite against these existing artifacts,
set `CORC` to the compiler, `OUT` and `LD_LIBRARY_PATH` to the library directory,
and `SKIP_LIBRARY_BUILD=1` before running
`tests/integration/shared-libraries.sh`. The skip prevents the legacy monolithic
library builder from replacing the artifacts under test. Use a private copy
when another build may update the original directory.
