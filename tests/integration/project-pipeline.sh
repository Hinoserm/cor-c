#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-pipeline.XXXXXX")"
cp tests/integration/project/Basic.csproj tests/integration/project/Program.cs tests/integration/project/Answer.cs "$work/"
corc="${CORC_DLL:-$root/compiler/bin/managed/Release/net10.0/corc.dll}"
run_build() { dotnet "$corc" project "$work/Basic.csproj" --jobs 4; }
run_build > "$work/first.log" 2>&1
grep -q 'Answer.cs unit-allocated=[0-9]* rebuilt: first build$' "$work/first.log"
program="$work/bin/cor-c/Release/net10.0/Basic"
status=0; "$program" || status=$?
test "$status" = 42
before="$(stat -c '%y' "$program")"
run_build > "$work/unchanged.log" 2>&1
grep -q '0/2 source units rebuilt' "$work/unchanged.log"
test "$(stat -c '%y' "$program")" = "$before"
# Change only an ordinary method body in the private fixture copy.
sed -i 's/=> 42;/=> 43;/' "$work/Answer.cs"
run_build > "$work/body.log" 2>&1
grep -q '1/2 source units rebuilt' "$work/body.log"
# And the build says why it compiled the one it did.
grep -q 'Answer.cs unit-allocated=[0-9]* rebuilt: source changed$' "$work/body.log"
status=0; "$program" || status=$?
test "$status" = 43
echo "PASS native .csproj execution, unchanged outputs and selective body rebuild with its reason: $work"
