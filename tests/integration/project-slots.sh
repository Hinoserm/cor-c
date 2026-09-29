#!/usr/bin/env bash
# Every unit of a project numbers a class's own virtual slots alike. A unit
# holding an interface the compiler made for itself -- a local function's
# delegate -- used to open the project's interface region and number every
# class's virtuals 129 higher than the units without one: the link stopped
# on a managed layout conflict over a public record's Equals(R?).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-slots.XXXXXX")"
cp tests/integration/project-slots/Slots.csproj tests/integration/project-slots/Program.cs \
   tests/integration/project-slots/Shapes.cs tests/integration/project-slots/Scanner.cs "$work/"
corc="${CORC_DLL:-$root/build/native-project-host/corc.dll}"
dotnet "$corc" project "$work/Slots.csproj" --jobs 4 > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
status=0; "$work/bin/cor-c/Release/net10.0/Slots" || status=$?
test "$status" = 41 || { echo "FAIL: exit $status, want 41"; exit 1; }
echo "PASS one numbering of class virtual slots across units: $work"
