#!/usr/bin/env bash
# A METHOD MORE OR LESS IN ONE INTERFACE: the units that read the interface
# are rebuilt, and no other -- each project family is numbered in a block of
# its own (InterfaceFamilies.Block) and the project's classes below them all
# (Binder's numbering), so no other family's slots, and no class's, move.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-interface-change.XXXXXX")"
sed 's|<Compile Include="Program.cs" /><Compile Include="Answer.cs" />|<Compile Include="Program.cs" /><Compile Include="Faces.cs" /><Compile Include="UseA.cs" />|' \
    tests/integration/project/Basic.csproj > "$work/Basic.csproj"
cat > "$work/Faces.cs" <<'CS'
namespace ProjectFixture;

public interface IAlpha { int One(); int Two(); int Three(); }
public interface IBeta { int Get(); }
public class Base { public virtual int V() => 1; }
CS
cat > "$work/UseA.cs" <<'CS'
namespace ProjectFixture;

public class A : IAlpha { public int One() => 1; public int Two() => 2; public int Three() => 3; }
public static class UseA { public static int Sum(IAlpha a) => a.One() + a.Two(); }
CS
cat > "$work/Program.cs" <<'CS'
namespace ProjectFixture;

public class B : Base, IBeta { public int Get() => 40; public override int V() => 2; }
public static class Program
{
    public static int Main() { IBeta b = new B(); Base c = new B(); return b.Get() + c.V(); }
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
step first 3/3 42
sed -i 's/ int Three(); }/ }/' "$work/Faces.cs"; sed -i 's/ public int Three() => 3;//' "$work/UseA.cs"
step method-removed 2/3 42
sed -i 's/ int Two(); }/ int Two(); int Four(); int Five(); }/' "$work/Faces.cs"
sed -i 's/ public int Two() => 2;/ public int Two() => 2; public int Four() => 4; public int Five() => 5;/' "$work/UseA.cs"
step methods-added 2/3 42
echo "PASS an interface's methods rebuild only its readers: $work"
