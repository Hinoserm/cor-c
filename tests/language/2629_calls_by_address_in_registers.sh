#!/usr/bin/env bash
#
# A function has one entry, and a call by its address passes its words in
# registers; the one marked [StackArguments] takes them from the stack.
#
#     tests/language/2629_calls_by_address_in_registers.sh
#
# The half of test 2629 its output cannot show: the image itself, compiled
# for x86 (the K8). No function has a second entry (no NAME$r beside a
# NAME, the stub and body of before); the virtual calls of Main -- `call
# *off(reg)` -- of three words or fewer push nothing before them; a function
# of three words reads no argument above its frame; and the function marked
# [StackArguments], under a name that is not one of this language's own
# calling (mc_), reads its two there.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/calls-by-address.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

if $corc compile "$here/2629_calls_by_address_in_registers.cor" -o "$work/calls" --cpu=athlon64 --disable-3dnow > "$work/calls.log" 2>&1
then pass "the program compiles"
else fail "the program compiles"; cat "$work/calls.log"; exit 1
fi

nm "$work/calls" | awk '$2 ~ /^[tT]$/ {print $3}' > "$work/symbols"
second=$(grep -c '\$r$' "$work/symbols")
if [ "$second" -eq 0 ]; then pass "no function has a second entry"
else fail "no function has a second entry ($second names end in \$r)"; fi

objdump -d --no-show-raw-insn "$work/calls" > "$work/calls.dis"
body() { awk -v name="$1" 'index($0, "<" name) && /:$/ {p=1; next} p && /^$/ {exit} p' "$work/calls.dis"; }

# Main's calls through a descriptor's slot: none with a push among the four
# instructions before it but the one of four words (this and three).
body m_Program_Main > "$work/main.dis"
slots=$(grep -cE 'call +\*0x[0-9a-f]+\(%e..\)' "$work/main.dis")
pushed=$(awk '/push / {seen = 4} /call +\*0x[0-9a-f]+\(%e..\)/ { if (seen > 0) n++ } { if (seen > 0) seen-- } END {print n + 0}' "$work/main.dis")
if [ "$slots" -ge 4 ]; then pass "Main calls through descriptors' slots ($slots)"
else fail "Main calls through descriptors' slots ($slots found)"; fi
if [ "$pushed" -eq 0 ]; then pass "none of them after a push"
else fail "none of them after a push ($pushed of $slots)"; fi

# A function of three words: no read of an argument above the frame.
body m_Program_Digits > "$work/digits.dis"
if [ -s "$work/digits.dis" ] && ! grep -qE '0x(8|c|10)\(%ebp\)|0x(4|8|c)\(%esp\)' "$work/digits.dis"
then pass "a function of three words reads none from the stack"
else fail "a function of three words reads none from the stack"; cat "$work/digits.dis"; fi

# The one marked [StackArguments]: its own kind of name, and both read there.
body mc_Program_Pushed > "$work/pushed.dis"
if [ -s "$work/pushed.dis" ]; then pass "the [StackArguments] method has a name of its own kind (mc_)"
else fail "the [StackArguments] method has a name of its own kind (mc_)"; grep -i pushed "$work/symbols"; fi
reads=$(grep -cE '0x(8|c)\(%ebp\)|0x(4|8)\(%esp\)' "$work/pushed.dis")
if [ "$reads" -ge 2 ]; then pass "it reads its two words from the stack"
else fail "it reads its two words from the stack ($reads reads)"; cat "$work/pushed.dis"; fi

if [ "$failures" -eq 0 ]; then echo "PASS 2629_calls_by_address_in_registers"; exit 0; fi
echo "FAIL 2629_calls_by_address_in_registers ($failures)"; exit 1
