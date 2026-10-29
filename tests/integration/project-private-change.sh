#!/usr/bin/env bash
# A PRIVATE MEMBER CHANGED IN ONE UNIT: the units compiled against its type
# stay current (SourceIndexBuilder.Hiding), and link with the new object
# (ManagedLayouts: a private field by its place and type, a private method
# not at all). A new instance field, a field's type, a const, the first
# static with an initialiser, or a public member rebuilds them.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-private-change.XXXXXX")"
cp tests/integration/project/Basic.csproj "$work/"
cat > "$work/Answer.cs" <<'CS'
namespace ProjectFixture;

public class Answer
{
    private int _value = 40;
    public int Read() => _value + 2;
}
CS
cat > "$work/Program.cs" <<'CS'
namespace ProjectFixture;

public static class Program
{
    public static int Main() => new Answer().Read();
}
CS
corc="${CORC_DLL:-$root/compiler/bin/managed/Release/net10.0/corc.dll}"
program="$work/bin/cor-c/Release/net10.0/Basic"
step() # NAME UNITS STATUS
{
    dotnet "$corc" project "$work/Basic.csproj" --jobs 4 --lib-root "$root" > "$work/$1.log" 2>&1 || true
    grep -q "$2 source units rebuilt" "$work/$1.log" || { echo "$1: not $2 rebuilt" >&2; exit 1; }
    status=0; "$program" || status=$?
    test "$status" = "$3" || { echo "$1: exit $status, not $3" >&2; exit 1; }
}
step first 2/2 42
sed -i 's/    public int Read/    private static int Helper() => 1;\n    private int Other() => 3;\n    public int Read/' "$work/Answer.cs"
step private-methods 1/2 42
sed -i 's/private int Other() => 3;/private long Other(int x) => 3;/' "$work/Answer.cs"
step private-signature 1/2 42
sed -i 's/_value/_renamed/g' "$work/Answer.cs"
step field-renamed 1/2 42
sed -i 's/= 40;/= 41;/' "$work/Answer.cs"
step field-initialiser 1/2 43
sed -i 's/    private int _renamed = 41;/    private int _renamed = 41;\n    private long _more;/' "$work/Answer.cs"
step field-added 2/2 43
sed -i 's/private long _more;/private int _more;/' "$work/Answer.cs"
step field-retyped 2/2 43
sed -i 's/    public int Read/    private const int Limit = 1;\n    public int Read/' "$work/Answer.cs"
step const-added 2/2 43
sed -i 's/    public int Read/    private static int[] _table = new int[1];\n    public int Read/' "$work/Answer.cs"
step static-initialised 2/2 43
sed -i 's/    public int Read/    private static int _plain;\n    private static string _second = "x";\n    public int Read/' "$work/Answer.cs"
step statics-added 1/2 43
sed -i 's/    public int Read/    public int Pub() => 5;\n    public int Read/' "$work/Answer.cs"
step public-method 2/2 43
echo "PASS private members leave other units current: $work"
