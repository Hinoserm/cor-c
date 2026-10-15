#!/bin/bash
# dncheck.sh TEST.cor [WORK]: runs a language test's program -- and the files
# its "// units:" line names -- under real .NET, and compares what it prints
# and its exit status with the test's expect-output and expect-exit. A test
# that reads the runtime's counters (Gc.Frees and the rest) gets GcShim.cs,
# under which every "freed" answer is True and only the computed numbers are
# compared. WORK (default ~/tmp/dncheck) holds a project a test.
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
f=$1; n=$(basename "$f" .cor); R=$(cd "$(dirname "$f")/../.." && pwd); d=${2:-$HOME/tmp/dncheck}/$n
rm -rf "$d"; mkdir -p "$d"; cp "$here/canonis.csproj" "$d/"
sed -n '/^\(using\|namespace\|public\|static\|class\|struct\|record\|interface\|enum\|delegate\|internal\|sealed\|abstract\)/,$p' "$f" > "$d/Program.cs"
grep -q "Gc\." "$f" && cp "$here/GcShim.cs" "$d/"
for u in $(sed -n 's|^// units: *||p' "$f"); do cp "$R/$u" "$d/$(basename "$u" .cor).cs"; done
exit_want=$(sed -n 's|^// expect-exit: *||p' "$f" | head -1)
awk '/^\/\/ expect-output:/{on=1; next} on && /^\/\/ ?$/{exit} on && /^\/\//{sub(/^\/\/ ?/,""); print; next} on && !/^\/\//{exit}' "$f" > "$d/want.txt"
(cd "$d" && timeout 300 dotnet run -v q --nologo 2> err.txt | grep -v ": warning CS" > got.txt; echo "${PIPESTATUS[0]}" > rc.txt)
if [ "$(cat "$d/rc.txt")" != "${exit_want:-0}" ]; then echo "EXIT $n: .NET exit $(cat "$d/rc.txt"), test says ${exit_want:-0}: $(grep -m3 error "$d/got.txt" "$d/err.txt" | head -3)"; fi
if diff -q "$d/want.txt" "$d/got.txt" > /dev/null; then echo "OK $n"; else echo "DIFF $n"; diff "$d/want.txt" "$d/got.txt" | head -8; fi
