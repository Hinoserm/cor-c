#!/usr/bin/env bash
#
# A function has one entry, and a call by its address passes its words in
# registers; the one marked [StackArguments] takes them from the stack.
#
#     tests/language/2629_calls_by_address_in_registers.sh
#
# The half of test 2629 its output cannot show: the image itself, compiled
# for x86 (the K8). No function has a second entry (no NAME$r beside a
# NAME, the stub and body of before); a function of three words reads no
# argument above its frame, and one of five reads two there -- its fourth
# and fifth, the first three in registers still; and the function marked
# [StackArguments], under a name that is not one of this language's own
# calling (mc_), reads both of its two there.
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

# Main's calls through a descriptor's slot: a push among the four
# instructions before one only at the two that pass more than three words
# or a long (Long.Four with its object, Long.Wide) -- no word that belongs
# in a register is pushed.
body m_Program_Main > "$work/main.dis"
slots=$(grep -cE 'call +\*0x[0-9a-f]+\(%e..\)' "$work/main.dis")
pushed=$(awk '/push / {seen = 4} /call +\*0x[0-9a-f]+\(%e..\)/ { if (seen > 0) n++ } { if (seen > 0) seen-- } END {print n + 0}' "$work/main.dis")
if [ "$slots" -ge 6 ]; then pass "Main calls through descriptors' slots ($slots)"
else fail "Main calls through descriptors' slots ($slots found)"; fi
if [ "$pushed" -le 2 ]; then pass "only the two of more than three words or a long follow a push ($pushed)"
else fail "only the two of more than three words or a long follow a push ($pushed of $slots do)"; fi

# A function of three words: no read of an argument above the frame.
body m_Program_Digits > "$work/digits.dis"
if [ -s "$work/digits.dis" ] && ! grep -qE '0x(8|c|10)\(%ebp\)|0x(4|8|c)\(%esp\)' "$work/digits.dis"
then pass "a function of three words reads none from the stack"
else fail "a function of three words reads none from the stack"; cat "$work/digits.dis"; fi

# A function of five words: its fourth and fifth from the stack, no more.
body m_Program_Five > "$work/five.dis"
low=$(grep -cE '0x(8|c)\(%ebp\)' "$work/five.dis"); high=$(grep -cE '0x(10|14|18)\(%ebp\)' "$work/five.dis")
if [ "$low" -ge 2 ] && [ "$high" -eq 0 ]; then pass "a function of five words reads its last two from the stack and no other"
else fail "a function of five words reads its last two from the stack and no other ($low reads of the first two slots, $high above)"; cat "$work/five.dis"; fi

# The one marked [StackArguments]: its own kind of name, and both read there.
body mc_Program_Pushed > "$work/pushed.dis"
if [ -s "$work/pushed.dis" ]; then pass "the [StackArguments] method has a name of its own kind (mc_)"
else fail "the [StackArguments] method has a name of its own kind (mc_)"; grep -i pushed "$work/symbols"; fi
reads=$(grep -cE '0x(8|c)\(%ebp\)|0x(4|8)\(%esp\)' "$work/pushed.dis")
if [ "$reads" -ge 2 ]; then pass "it reads its two words from the stack"
else fail "it reads its two words from the stack ($reads reads)"; cat "$work/pushed.dis"; fi

if [ "$failures" -eq 0 ]; then echo "PASS 2629_calls_by_address_in_registers"; exit 0; fi
echo "FAIL 2629_calls_by_address_in_registers ($failures)"; exit 1
