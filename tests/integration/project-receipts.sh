#!/usr/bin/env bash
# What a unit is rebuilt for: the members of another unit's type it asked
# for by name, not the whole type (Dependence.Members); and a project copied
# elsewhere with its times rebuilds nothing (no path in any stamp or receipt).
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
  <ItemGroup><Compile Include="Program.cs" /><Compile Include="Util.cs" /><Compile Include="UseA.cs" /><Compile Include="UseB.cs" /></ItemGroup>
</Project>
XML
cat > "$work/Util.cs" <<'CS'
static class Util
{
    public static int A() => 1;
    public static int B() => 2;
    public const int C = 3;
}
CS
cat > "$work/UseA.cs" <<'CS'
static class UseA
{
    public static int Go() => Util.A();
}
CS
cat > "$work/UseB.cs" <<'CS'
static class UseB
{
    public static int Go() => Util.B() + Util.C;
}
CS
cat > "$work/Program.cs" <<'CS'
static class Program
{
    static int Main() => UseA.Go() * 10 + UseB.Go();
}
CS
program="$work/bin/cor-c/Release/net10.0/Receipts"
answer() { local status=0; "$1" || status=$?; echo "$status"; }
run_build "$work" > "$work/first.log" 2>&1
test "$(answer "$program")" = 15
# A member more in Util: Util alone is compiled again.
sed -i 's/    public const int C = 3;/    public const int C = 3;\n    public static int D() => 4;/' "$work/Util.cs"
run_build "$work" > "$work/added.log" 2>&1
grep -q '1/4 source units rebuilt' "$work/added.log"
grep -q 'Util.cs unit-allocated=[0-9]* rebuilt: source changed$' "$work/added.log"
# B's signature: UseB, which calls it, and not UseA, which does not.
sed -i 's/    public static int B() => 2;/    public static int B(int k = 1) => 2 + k;/' "$work/Util.cs"
run_build "$work" > "$work/signature.log" 2>&1
grep -q '2/4 source units rebuilt' "$work/signature.log"
grep -q 'UseB.cs unit-allocated=[0-9]* rebuilt: member B of Util changed$' "$work/signature.log"
test "$(answer "$program")" = 16
# The tree copied elsewhere, times and all: nothing compiled again.
cp -a "$work" "$work-copy"
run_build "$work-copy" > "$work-copy/copied.log" 2>&1
grep -q '0/4 source units rebuilt, 0/[0-9]* class library units' "$work-copy/copied.log"
test "$(answer "$work-copy/bin/cor-c/Release/net10.0/Receipts")" = 16
echo "PASS a member added rebuilds its own unit, a signature its callers', a copied tree nothing: $work"
