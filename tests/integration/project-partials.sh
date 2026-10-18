#!/usr/bin/env bash
# A project whose non-entry units each hold part of one partial class, and
# whose own Clock and Os share names with the runtime's: every unit builds,
# the program runs, and each name means what it means where it is written.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-partials.XXXXXX")"
cp tests/integration/project-partials/Partials.csproj tests/integration/project-partials/Program.cs \
   tests/integration/project-partials/Grow.cs tests/integration/project-partials/GrowWider.cs \
   tests/integration/project-partials/Clock.cs "$work/"
corc="${CORC_DLL:-$root/compiler/bin/managed/Release/net10.0/corc.dll}"
dotnet "$corc" project "$work/Partials.csproj" --jobs 2 > "$work/build.log" 2>&1
program="$work/bin/cor-c/Release/net10.0/Partials"
status=0; out="$("$program")" || status=$?
test "$status" = 42
test "$out" = "4 100 10 grow100!2 5 mine"
echo "PASS partial types across project units and program types named like the runtime's: $work"
