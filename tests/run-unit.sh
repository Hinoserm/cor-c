#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
projects=(asm elf opt x86)
for name in "${projects[@]}"; do
    dotnet run -c Release --project "$root/tests/unit/$name"/*tests.csproj
done
