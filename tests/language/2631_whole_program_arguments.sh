#!/usr/bin/env bash
#
# The arguments a whole program's calls pass, judged over all of it.
#
#     tests/language/2631_whole_program_arguments.sh
#
# The half of test 2631 its output cannot show: the image, compiled for x86
# (the K8). Step has a version (a name ending $with$N) for a number that
# decides its branch; Put has a way in that sets its kind (a name ending
# $entry$ and the parameter's bit), laid before Put and falling into it;
# Scale reads no third argument; Main passes Mix a pair's two words, not
# the pair's address; and Rewritten's count, which it writes, is gone.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/whole-program-arguments.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

if $corc compile "$here/2631_whole_program_arguments.cor" -o "$work/arguments" --cpu=athlon64 --disable-3dnow > "$work/arguments.log" 2>&1
then pass "the program compiles"
else fail "the program compiles"; cat "$work/arguments.log"; exit 1
fi

nm -n "$work/arguments" | awk '$2 ~ /^[tT]$/' > "$work/symbols"
# (Each name is the method's and its signature's: m_Program_Put_3_V$I32...)
if grep -qE ' m_Program_Step_[^ ]*\$with\$[0-9]+$' "$work/symbols"; then pass "Step has a version"
else fail "Step has a version"; grep Step "$work/symbols"; fi

# The way in: its name, and laid at most 16 bytes before Put's own.
entry=$(grep -E ' m_Program_Put_[^ ]*\$entry\$[0-9a-f]+$' "$work/symbols" | head -1)
put=$(grep -E ' m_Program_Put_[^ ]*$' "$work/symbols" | grep -vE '\$(entry|with)\$' | head -1)
if [ -n "$entry" ] && [ -n "$put" ]; then
    gap=$(( 0x$(echo "$put" | cut -d' ' -f1) - 0x$(echo "$entry" | cut -d' ' -f1) ))
    if [ "$gap" -gt 0 ] && [ "$gap" -le 16 ]; then pass "Put has a way in just before it ($gap bytes)"
    else fail "Put has a way in just before it ($gap bytes)"; fi
else fail "Put has a way in just before it"; grep Put "$work/symbols"; fi

objdump -d --no-show-raw-insn "$work/arguments" > "$work/arguments.dis"
body() { awk -v name="$1" 'index($0, "<" name ">:") {p=1; next} p && /^$/ {exit} p' "$work/arguments.dis"; }

# Scale(value, factor, unused): factor is 3 at every call and unused never
# read, so the function reads nothing above its frame.
scale=$(grep -oE ' m_Program_Scale_[^ ]*$' "$work/symbols" | grep -vE '\$(entry|with)\$' | head -1 | tr -d ' ')
body "$scale" > "$work/scale.dis"
if [ -s "$work/scale.dis" ] && ! grep -qE '0x(8|c|10)\(%ebp\)|0x(4|8|c)\(%esp\)' "$work/scale.dis"
then pass "Scale reads no argument from the stack"
else fail "Scale reads no argument from the stack"; cat "$work/scale.dis"; fi

# Main takes no pair's address for Mix: no lea of a frame slot just before
# a call of Mix.
body "$(grep -oE ' m_Program_Main_[^ ]*$' "$work/symbols" | head -1 | tr -d ' ')" > "$work/main.dis"
mix=$(grep -cE 'call .*<m_Program_Mix_' "$work/main.dis")
taken=$(awk '/lea .*\(%e(b|s)p\)/ { seen = 3; next } /call .*<m_Program_Mix_/ { if (seen > 0) n++ } { if (seen > 0) seen-- } END {print n + 0}' "$work/main.dis")
if [ "$mix" -ge 1 ] && [ "$taken" -eq 0 ]; then pass "Main passes Mix the pair's words ($mix calls)"
else fail "Main passes Mix the pair's words ($mix calls, $taken after an address)"; fi

# Rewritten's count, 4 at every call and written by the function, is gone:
# no way in sets it (which it would, were the count still passed).
if ! grep -qE ' m_Program_Rewritten_[^ ]*\$entry\$' "$work/symbols"; then pass "Rewritten's count is gone"
else fail "Rewritten's count is gone"; grep Rewritten "$work/symbols"; fi

if [ "$failures" -eq 0 ]; then echo "PASS 2631_whole_program_arguments"; exit 0; fi
echo "FAIL 2631_whole_program_arguments ($failures)"; exit 1
