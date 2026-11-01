#!/usr/bin/env bash
# What a unit is rebuilt for: the members of another unit's type it asked
# for by name, not the whole type (Dependence.Members); and a project copied
# elsewhere with its times rebuilds nothing (no path in any stamp or receipt).
# Util has a generic method: UseG, which calls it and so copies its body,
# depends on the whole file; the others still by member. Util.K names
# Other.X, which names Util.R: UseK, asking for K alone, is compiled again
# for R. UseX calls an extension method of another class, which once read
# every static class in reach whole, Util among them. Util's private statics
# are in its shape only by where the first that ends the run the link may
# make stands: one more or less, or two in another order, changes nothing; a
# declaration's second declarator that ends the run where none did changes
# it, as do an array's length (`?` and all) and a public declaration's text.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-receipts.XXXXXX")"
corc_dll="${CORC_DLL:-$root/compiler/bin/managed/Release/net10.0/corc.dll}"
# CORC, a wrapper that names the library root, where the compiler is not this tree's.
run_build() { if [ -n "${CORC:-}" ]; then "$CORC" project "$1/Receipts.csproj" --jobs 4; else dotnet "$corc_dll" project "$1/Receipts.csproj" --jobs 4; fi; }
cat > "$work/Receipts.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <StartupObject>Program</StartupObject>
  </PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" /><Compile Include="Util.cs" /><Compile Include="UseA.cs" /><Compile Include="UseB.cs" /><Compile Include="UseG.cs" /><Compile Include="Other.cs" /><Compile Include="UseK.cs" /><Compile Include="Ext.cs" /><Compile Include="UseX.cs" /></ItemGroup>
</Project>
XML
cat > "$work/Util.cs" <<'CS'
static class Util
{
    public static int A() => 1;
    public static int B() => 2;
    public const int C = 3;
    public const int S = C - 3;
    public const int P = 5, Q = 6;
    public static T First<T>(T a, T b) => a;
    public const int R = 5;
    public const int K = Other.X + 1;
    private static object _o = new object();
    private static string?[] _s = new string?[8000];
    private static int _a, _b = 1;
}
CS
cat > "$work/Ext.cs" <<'CS'
static class Ext
{
    public static int Twice(this int x) => x * 2;
}
CS
cat > "$work/UseX.cs" <<'CS'
static class UseX
{
    public static int Go() => Util.A() - 1 + 0.Twice();
}
CS
cat > "$work/Other.cs" <<'CS'
static class Other
{
    public const int X = Util.R + 1;
}
CS
cat > "$work/UseK.cs" <<'CS'
static class UseK
{
    public static int Go() => Util.K - 7;
}
CS
cat > "$work/UseA.cs" <<'CS'
static class UseA
{
    public static int Go() => Util.A() + Util.S;
}
CS
cat > "$work/UseB.cs" <<'CS'
static class UseB
{
    public static int Go() => Util.B() + Util.C + Util.Q - 6;
}
CS
cat > "$work/UseG.cs" <<'CS'
static class UseG
{
    public static int Go() => Util.First(4, 5) - 4;
}
CS
cat > "$work/Program.cs" <<'CS'
static class Program
{
    static int Main() => UseA.Go() * 10 + UseB.Go() + UseG.Go() + UseK.Go() + UseX.Go();
}
CS
program="$work/bin/cor-c/Release/net10.0/Receipts"
answer() { local status=0; "$1" || status=$?; echo "$status"; }
run_build "$work" > "$work/first.log" 2>&1
test "$(answer "$program")" = 15
# A member more in Util: Util, and UseG, which copied a body of it.
sed -i 's/    public const int C = 3;/    public const int C = 3;\n    public static int D() => 4;/' "$work/Util.cs"
run_build "$work" > "$work/added.log" 2>&1
grep -q '2/9 source units rebuilt' "$work/added.log"
grep -q 'Util.cs unit-allocated=[0-9]* rebuilt: source changed$' "$work/added.log"
# B's signature: UseB, which calls it, and not UseA, which does not.
sed -i 's/    public static int B() => 2;/    public static int B(int k = 1) => 2 + k;/' "$work/Util.cs"
run_build "$work" > "$work/signature.log" 2>&1
grep -q '3/9 source units rebuilt' "$work/signature.log"
grep -q 'UseB.cs unit-allocated=[0-9]* rebuilt: member B of Util changed$' "$work/signature.log"
test "$(answer "$program")" = 16
# A SECOND NAME OF ONE DECLARATION: Q, written after P in one line (one text, both names), is UseB's
# to read, and its value changed is UseB's to compile again.
sed -i 's/public const int P = 5, Q = 6;/public const int P = 5, Q = 7;/' "$work/Util.cs"
run_build "$work" > "$work/declarator.log" 2>&1
grep -qE 'UseB.cs unit-allocated=[0-9]* rebuilt: member [PQ] of Util changed$' "$work/declarator.log"
test "$(answer "$program")" = 17
grep -q '3/9 source units rebuilt' "$work/declarator.log"
# The generic method's body: UseG, which copied it, from the file's text; the
# units that read Util and not that body, from its shape, which holds what the
# template bodies name (SourceDeclaration.TemplateHash).
sed -i 's/public static T First<T>(T a, T b) => a;/public static T First<T>(T a, T b) => b;/' "$work/Util.cs"
run_build "$work" > "$work/template.log" 2>&1
grep -q "UseG.cs unit-allocated=[0-9]* rebuilt: text of Util's file changed" "$work/template.log"
grep -q 'UseA.cs unit-allocated=[0-9]* rebuilt: shape of Util changed$' "$work/template.log"
test "$(answer "$program")" = 18
# A CONSTANT ANOTHER NAMES: S = C - 3 is UseA's, C UseB's; C changed is both
# units' to compile again, though UseA never asked for C.
sed -i 's/public const int C = 3;/public const int C = 4;/' "$work/Util.cs"
run_build "$work" > "$work/constant.log" 2>&1
grep -q 'UseA.cs unit-allocated=[0-9]* rebuilt: member S of Util changed$' "$work/constant.log"
grep -q 'UseB.cs unit-allocated=[0-9]* rebuilt: member C of Util changed$' "$work/constant.log"
test "$(answer "$program")" = 29
# A CONSTANT OF UTIL'S NAMED BY ANOTHER TYPE'S CONSTANT THAT UTIL'S NAMES: R
# changed is UseK's to compile again, though it never named R or Other.
sed -i 's/public const int R = 5;/public const int R = 6;/' "$work/Util.cs"
run_build "$work" > "$work/chain.log" 2>&1
grep -q 'UseK.cs unit-allocated=[0-9]* rebuilt' "$work/chain.log"
test "$(answer "$program")" = 30
# A PRIVATE STATIC MORE, AND ONE TAKEN AWAY (Ide's shutdown list): Util, and
# UseG, which copied a body of it; no unit that reads Util by member.
sed -i 's/    private static object _o = new object();/    private static object _o = new object();\n    private static object _p = new object();/' "$work/Util.cs"
run_build "$work" > "$work/private.log" 2>&1
grep -q '2/9 source units rebuilt' "$work/private.log"
sed -i '/    private static object _p = new object();/d' "$work/Util.cs"
run_build "$work" > "$work/removed.log" 2>&1
grep -q '2/9 source units rebuilt' "$work/removed.log"
# AN ARRAY'S LENGTH: 8000 strings are a zeroed table, 9000 code, and every
# unit touching Util reads which.
sed -i 's/new string?\[8000\]/new string?[9000]/' "$work/Util.cs"
run_build "$work" > "$work/length.log" 2>&1
grep -q 'UseA.cs unit-allocated=[0-9]* rebuilt: shape of Util changed$' "$work/length.log"
test "$(answer "$program")" = 30
# A SECOND DECLARATOR'S INITIALISER, its first without one: `_b = A()` is
# startup work that ends the run the link may make, `_b = 1` not.
sed -i 's/private static int _a, _b = 1;/private static int _a, _b = A();/' "$work/Util.cs"
run_build "$work" > "$work/declarators.log" 2>&1
grep -q 'UseA.cs unit-allocated=[0-9]* rebuilt: shape of Util changed$' "$work/declarators.log"
test "$(answer "$program")" = 30
# THE SAME TWO IN THE OTHER ORDER: the run the link may make ends in this
# declaration either way, and no public static stands between them: Util and
# UseG alone.
sed -i 's/private static int _a, _b = A();/private static int _a = 1, _b = A();/' "$work/Util.cs"
run_build "$work" > "$work/order1.log" 2>&1
grep -q '2/9 source units rebuilt' "$work/order1.log"
sed -i 's/private static int _a = 1, _b = A();/private static int _a = A(), _b = 1;/' "$work/Util.cs"
run_build "$work" > "$work/order2.log" 2>&1
grep -q '2/9 source units rebuilt' "$work/order2.log"
# A PUBLIC ONE, its first declarator without an initialiser: the whole
# declaration is the shape's, not the first name's text.
sed -i 's/    public const int R = 6;/    public const int R = 6;\n    public static int V, W = 1;/' "$work/Util.cs"
run_build "$work" > "$work/public1.log" 2>&1
sed -i 's/public static int V, W = 1;/public static int V, W = A();/' "$work/Util.cs"
run_build "$work" > "$work/public2.log" 2>&1
grep -q 'UseA.cs unit-allocated=[0-9]* rebuilt: shape of Util changed$' "$work/public2.log"
test "$(answer "$program")" = 30
# A PRIVATE STATIC THAT ENDS THE RUN MOVED PAST A PUBLIC ONE THE LINK MAY
# MAKE: where it stands among them is what the shape says of it.
sed -i 's/    private static int _a = A(), _b = 1;/&\n    public static readonly object Pub = new object();/' "$work/Util.cs"
run_build "$work" > "$work/pub.log" 2>&1
grep -q 'public static readonly object Pub' "$work/Util.cs"
sed -i '/    private static int _a = A(), _b = 1;/d' "$work/Util.cs"
sed -i 's/    public static readonly object Pub = new object();/&\n    private static int _a = A(), _b = 1;/' "$work/Util.cs"
run_build "$work" > "$work/crossed.log" 2>&1
grep -q 'UseA.cs unit-allocated=[0-9]* rebuilt: shape of Util changed$' "$work/crossed.log"
test "$(answer "$program")" = 30
# The tree copied elsewhere, times and all: nothing compiled again.
cp -a "$work" "$work-copy"
run_build "$work-copy" > "$work-copy/copied.log" 2>&1
grep -q '0/9 source units rebuilt, 0/[0-9]* class library units' "$work-copy/copied.log"
test "$(answer "$work-copy/bin/cor-c/Release/net10.0/Receipts")" = 30
echo "PASS a member added rebuilds its own unit, a signature its callers', a copied tree nothing: $work"
