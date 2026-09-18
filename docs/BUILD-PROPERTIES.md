# Command-line build properties

`build [target/path] [Name=Value ...]` accepts named orchestration properties in
any position. For example, `build disk=output.bin arch=486 smp=0` selects the
manifest's default target and overrides three declared defaults. `build configure
arch=486 smp=0` selects the named configuration target without building a disk.

Names are case-insensitive identifiers. The first equals sign separates name
from value; subsequent equals signs belong to the value. Quote arguments that
contain spaces. Values are passed as argument-list entries, not interpolated
shell commands. `--property Name=Value` remains available.

Manifests may set `StrictProperties="true"` on `Build` to reject command-line
names not declared by a `PropertyGroup` (except the standard `Configuration`).
Hardware build manifests should use this so a typo does not silently select
the wrong drivers. Compiler-specific flags are not inferred from property names;
the manifest decides how properties are passed to configuration and build tasks.
