#!/usr/bin/env bash
# A DECLARATION CHANGED IN ONE UNIT, USED BY ANOTHER: the project build
# compiles the dependent unit against the new declaration, and a unit whose
# source is not the text the declaration index was made from is refused
# with the index named as out of date -- never compiled against its
# neighbours' old declarations (ProjectCompile, SourceIndexBuilder.SourceKey).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-declaration-change.XXXXXX")"
cp tests/integration/project/Basic.csproj tests/integration/project/Program.cs tests/integration/project/Answer.cs "$work/"
corc="${CORC_DLL:-$root/build/native-project-host/corc.dll}"
run_build() { dotnet "$corc" project "$work/Basic.csproj" --jobs 4; }
run_build > "$work/first.log" 2>&1
program="$work/bin/cor-c/Release/net10.0/Basic"
status=0; "$program" || status=$?
test "$status" = 42

# A new member in one unit, used by the other, both edited between builds.
sed -i 's/public static int Read() => 42;/public static int Read() => 42;\n    public static int Twice() => 84;/' "$work/Answer.cs"
sed -i 's/Answer.Read()/Answer.Twice()/' "$work/Program.cs"
run_build > "$work/declaration.log" 2>&1
grep -q '2/2 source units rebuilt' "$work/declaration.log"
status=0; "$program" || status=$?
test "$status" = 84

# A source edited after the index was made: compiled through compile-project
# against that index, it is refused as out of date, not compiled against the
# old declarations.
cat > "$work/Value.cor" <<'COR'
namespace StaleLib;
public static class Value
{
    public static int Get() => 42;
}
COR
cat > "$work/Caller.cor" <<'COR'
using StaleLib;
static class Program
{
    static int Main() => Value.Get();
}
COR
dotnet "$corc" index --assembly Stale "$work/Value.cor" "$work/Caller.cor" -o "$work/stale.idx" > "$work/index.log" 2>&1
sed -i 's/public static int Get() => 42;/public static int Get() => 42;\n    public static int More() => 7;/' "$work/Value.cor"
sed -i 's/Value.Get()/Value.More()/' "$work/Caller.cor"
printf '%s\t%s\t%s\tentry\n' "$work/Caller.cor" "$work/caller.o" "$work/caller.o.deps" > "$work/units.tsv"
if dotnet "$corc" compile-project --units "$work/units.tsv" --decl-index "$work/stale.idx" --assembly Stale --nostdlib --obj > "$work/stale.log" 2>&1; then
    echo 'A unit edited after its index was made was compiled against it' >&2; exit 1
fi
grep -q 'declaration index is out of date' "$work/stale.log"
if grep -q "has no member" "$work/stale.log"; then
    echo 'The stale unit was compiled against old declarations' >&2; exit 1
fi
echo "PASS declaration change across units, and a source newer than its index refused: $work"
