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

# Main's calls through a descriptor's slot: a push among the six
# instructions before one at exactly the five that pass more than three
# words or a long -- Shape.Three and Long.Four with their object, the
# delegates of three and of five arguments with theirs, and Long.Wide: the
# words past the third pushed, then the registers set. No word that belongs
# in a register is pushed, and those five are found.
body m_Program_Main > "$work/main.dis"
slots=$(grep -cE 'call +\*0x[0-9a-f]+\(%e..\)' "$work/main.dis")
pushed=$(awk '/call +\*0x[0-9a-f]+\(%e..\)/ { if (seen > 0) n++; seen = 0; next } /push / { seen = 6; next } { if (seen > 0) seen-- } END {print n + 0}' "$work/main.dis")
if [ "$slots" -ge 6 ]; then pass "Main calls through descriptors' slots ($slots)"
else fail "Main calls through descriptors' slots ($slots found)"; fi
if [ "$pushed" -eq 5 ]; then pass "exactly the five of more than three words or a long follow a push"
else fail "exactly the five of more than three words or a long follow a push ($pushed of $slots do)"; fi

# Where a function's stack arguments begin: framed, EBP+8; a leaf (no
# `mov ebp, esp`) has no frame, and its arguments are off ESP, past the
# registers it pushed (P) and the frame it reserved (sub esp, N): the
# return address at ESP+4P+N, the first argument four above.
first_arg() {
    if grep -qE 'mov +%esp,%ebp' "$1"; then echo ebp 8; return; fi
    p=$(awk '/push +%e/ { n++; next } { exit } END { print n + 0 }' "$1")
    # (Only a sub straight after the pushes: the frame, not a call's space.)
    n=$(awk '/push +%e/ { next } /sub +\$0x[0-9a-f]+,%esp/ { sub(/.*\$0x/, ""); sub(/,.*/, ""); print } { exit }' "$1")
    echo esp $((4 * p + 0x${n:-0} + 4))
}
# Every read of a stack argument, as offsets from the first.
stack_reads() {
    set -- "$1" $(first_arg "$1")
    # (A displacement with a minus is a local's, below the frame: never one.)
    grep -oE "[-]?(0x[0-9a-f]+)?\(%$2\)" "$1" | grep -v '^-' | sed -E 's/\(.*//; s/^$/0x0/' | while read -r d; do
        o=$((d - $3)); [ $o -ge 0 ] && echo $o
    done
}

# A function of three words: no read of an argument above the frame.
body m_Program_Digits > "$work/digits.dis"
if [ -s "$work/digits.dis" ] && [ -z "$(stack_reads "$work/digits.dis")" ]
then pass "a function of three words reads none from the stack"
else fail "a function of three words reads none from the stack"; cat "$work/digits.dis"; fi

# A function of five words: its fourth and fifth from the stack, no more.
body m_Program_Five > "$work/five.dis"
reads=$(stack_reads "$work/five.dis")
low=$(printf '%s\n' $reads | grep -cxE '0|4'); high=$(printf '%s\n' $reads | grep -cvxE '0|4|')
if [ "$low" -ge 2 ] && [ "$high" -eq 0 ]; then pass "a function of five words reads its last two from the stack and no other"
else fail "a function of five words reads its last two from the stack and no other ($low reads of the first two slots, $high elsewhere)"; cat "$work/five.dis"; fi

# The one marked [StackArguments]: its own kind of name, and both read there.
body mc_Program_Pushed > "$work/pushed.dis"
if [ -s "$work/pushed.dis" ]; then pass "the [StackArguments] method has a name of its own kind (mc_)"
else fail "the [StackArguments] method has a name of its own kind (mc_)"; grep -i pushed "$work/symbols"; fi
reads=$(grep -cE '0x(8|c)\(%ebp\)|0x(4|8)\(%esp\)' "$work/pushed.dis")
if [ "$reads" -ge 2 ]; then pass "it reads its two words from the stack"
else fail "it reads its two words from the stack ($reads reads)"; cat "$work/pushed.dis"; fi

if [ "$failures" -eq 0 ]; then echo "PASS 2629_calls_by_address_in_registers"; exit 0; fi
echo "FAIL 2629_calls_by_address_in_registers ($failures)"; exit 1
