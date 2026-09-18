# Standard-library sources

Portable public APIs are organized by namespace under `System/`. Internal host
adapters live under `platforms/<platform>/` without changing public API names.
Only target-dependent implementation details belong there; applications always
use the ordinary `System` namespaces.
