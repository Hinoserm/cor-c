#!/usr/bin/env bash
#
# The half of tests 2329 and 2330 their output cannot show: the shapes of
# the functions in long mode's image, which the runtime reads byte for byte
# when one faults (Runtime.FrameAt, Prologue). If the allocator stopped
# giving these functions the registers a call keeps, the two tests would
# still pass and test nothing of what they are for.
#
#   2329 Wide     a leaf: begins with pushes of registers a call keeps, never
#                 `push rbp ; mov rbp, rsp`, and has no `leave`
#   2329 Wider    a leaf that gives RBP a value: `push rbp` first and no
#                 `mov rbp, rsp` after it; reserves slots with `sub rsp`
#   2330 Checked, CheckedWider
#                 a reserved frame: `sub rsp` first, then pushes; on the way
#                 to the bounds check `mov [rsp+X], rbp ; lea rbp, [rsp+X]`
#                 with one X, then the call; and the stack a multiple of
#                 sixteen there
#
#     tests/language/2330_reserved_cold_frames_saving_registers.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/frameless-shapes.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

for t in 2329_frameless_leaf_faults_with_saved_registers 2330_reserved_cold_frames_saving_registers; do
    if $corc compile "$here/$t.cor" -o "$work/${t%%_*}" --target x86-64 --cpu=athlon64 > "$work/${t%%_*}.log" 2>&1
    then pass "${t%%_*} compiles for long mode"
    else fail "${t%%_*} compiles for long mode"; cat "$work/${t%%_*}.log"; exit 1
    fi
done

# A function's instructions, mnemonics and operands, one a line.
body() {
    local image=$1 name=$2 at size
    at=$(nm "$image" | awk -v n=" m_Program_${name}_" 'index($0, n) { print $1; exit }')
    size=$(nm -S "$image" | awk -v n=" m_Program_${name}_" 'index($0, n) { print $2; exit }')
    [ -n "$at" ] || return 1
    objdump -d --no-show-raw-insn -M intel "$image" --start-address=0x$at --stop-address=$(printf '0x%x' $((0x$at + 0x$size))) \
        | awk 'NR > 7 { sub(/^ *[0-9a-f]+:[ \t]*/, ""); gsub(/[ \t]+/, " "); print }'
}

wide=$(body "$work/2329" Wide) || fail "Wide is in the image"
wider=$(body "$work/2329" Wider) || fail "Wider is in the image"
checked=$(body "$work/2330" Checked) || fail "Checked is in the image"
checkedWider=$(body "$work/2330" CheckedWider) || fail "CheckedWider is in the image"

first() { echo "$1" | sed -n "$2p"; }
if first "$wide" 1 | grep -qE '^push r(bx|1[2-5])$'; then pass "Wide begins by pushing a register a call keeps"; else fail "Wide begins by pushing a register a call keeps"; first "$wide" 1; fi
if echo "$wide" | grep -qE '^(leave|mov rbp,rsp)$'; then fail "Wide has no frame"; else pass "Wide has no frame"; fi
if echo "$wide" | grep -qE '^push r1[2-5]$'; then pass "Wide pushes one of R12 to R15"; else fail "Wide pushes one of R12 to R15"; fi

if [ "$(first "$wider" 1)" = "push rbp" ] && [ "$(first "$wider" 2)" != "mov rbp,rsp" ]; then pass "Wider pushes RBP first and makes no frame of it"; else fail "Wider pushes RBP first and makes no frame of it"; echo "$wider" | head -3; fi
if echo "$wider" | grep -qE '^sub rsp,0x[0-9a-f]+$'; then pass "Wider reserves its slots"; else fail "Wider reserves its slots"; fi
if echo "$wider" | grep -q '^leave$'; then fail "Wider has no leave"; else pass "Wider has no leave"; fi

for name in Checked CheckedWider; do
    code=$checked; [ $name = CheckedWider ] && code=$checkedWider
    if first "$code" 1 | grep -qE '^(sub rsp,0x[0-9a-f]+|push rax)$' && first "$code" 2 | grep -qE '^push r'; then pass "$name reserves the frame pointer's word, then pushes"; else fail "$name reserves the frame pointer's word, then pushes"; echo "$code" | head -3; fi
    made=$(echo "$code" | grep -n -A2 -E '^mov QWORD PTR \[rsp\+0x[0-9a-f]+\],rbp$' | head -3)
    x1=$(echo "$made" | sed -n 1p | grep -oE '0x[0-9a-f]+')
    x2=$(echo "$made" | sed -n 2p | grep -oE 'lea rbp,\[rsp\+0x[0-9a-f]+\]' | grep -oE '0x[0-9a-f]+')
    if [ -n "$x1" ] && [ "$x1" = "$x2" ] && echo "$made" | sed -n 3p | grep -q 'call'; then pass "$name makes its frame at one place on the way to its call"; else fail "$name makes its frame at one place on the way to its call"; echo "$made"; fi
    # From the return address down: the frame pointer's word, then X bytes
    # of slots, saves and padding. The stack was eight short of a sixteen
    # on the way in, the word made that up: X must be whole sixteens.
    if [ -n "$x1" ] && [ $(( (x1 + 16) % 16 )) -eq 0 ]; then pass "$name calls with the stack a multiple of sixteen"; else fail "$name calls with the stack a multiple of sixteen ($x1)"; fi
    if echo "$code" | grep -qE '^(leave|push rbp)$'; then fail "$name has no frame on the way that returns"; else pass "$name has no frame on the way that returns"; fi
done

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
