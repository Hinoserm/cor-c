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
    dotnet run -c Release --project "$project"
done
