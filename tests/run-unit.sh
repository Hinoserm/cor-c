#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
projects=(
    "$root/compiler/tests/optimizations/OptTests.csproj"
    "$root/linker/tests/ElfTests.csproj"
    "$root/compiler/tests/arch/corsac/asmtests.csproj"
    "$root/compiler/tests/arch/x86/x86tests.csproj"
)
for project in "${projects[@]}"; do
    "${CORC:-$root/compiler/bin/Release/net10.0/corc}" project "$project" --configuration Release
    directory="$(dirname "$project")"
    name="$(basename "$project" .csproj)"
    "$directory/bin/cor-c/Release/net10.0/$name"
done
