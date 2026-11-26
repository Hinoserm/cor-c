#!/usr/bin/env bash
#
# The half of test 2333 its output cannot show: the image, both targets. A
# device's writes and reads (Sys.Write8..64, Sys.Read8..64) are all there
# and in the source's order among every other access of memory.
#
#   Ring      memory, register, register, memory, register: five stores,
#             the first register write not dropped for the second
#   Status    memory, register, memory: three loads, the third no copy of
#             the first
#   Widths    the read nobody uses is made
#   Polled    the memory's load stays in the loop, after the register's write
#   Flagged   the flag in memory is compared on both sides of the read
#   Local     a local as the register: both writes made in its slot
#
#     tests/language/2333_device_writes_kept_in_order.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/device-writes.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

# A function's instructions, mnemonics and operands, one a line.
body() {
    local image=$1 name=$2 at size
    at=$(nm "$image" | awk -v n=" m_Program_${name}_" 'index($0, n) { print $1; exit }')
    size=$(nm -S "$image" | awk -v n=" m_Program_${name}_" 'index($0, n) { print $2; exit }')
    [ -n "$at" ] || return 1
    objdump -d --no-show-raw-insn -M intel "$image" --start-address=0x$at --stop-address=$(printf '0x%x' $((0x$at + 0x$size))) \
        | awk 'NR > 7 { sub(/^ *[0-9a-f]+:[ \t]*/, ""); gsub(/[ \t]+/, " "); print }'
}
# The base register of each store (a memory destination), in order, as letters: a for the first seen, b the next.
stores() { grep -oE '^mov [A-Z]+ PTR \[[a-z0-9]+' | grep -oE '[a-z0-9]+$' | awk '{ if (!($1 in seen)) seen[$1] = sprintf("%c", 97 + n++); printf "%s", seen[$1] } END { print "" }'; }
# The same of each load (a memory source; i386 may add the last straight from memory).
loads() { grep -oE '^(mov[a-z]*|add) [a-z0-9]+,[A-Z]+ PTR \[[a-z0-9]+' | grep -oE '[a-z0-9]+$' | awk '{ if (!($1 in seen)) seen[$1] = sprintf("%c", 97 + n++); printf "%s", seen[$1] } END { print "" }'; }

for target in x86 x86-64; do
    flags="--cpu=athlon64"; [ $target = x86 ] && flags="$flags --disable-3dnow"
    image="$work/device-$target"
    if $corc compile "$here/2333_device_writes_kept_in_order.cor" -o "$image" --target $target $flags > "$image.log" 2>&1
    then pass "$target: the program compiles"
    else fail "$target: the program compiles"; cat "$image.log"; continue
    fi
    ring=$(body "$image" Ring) || fail "$target: Ring is in the image"
    status=$(body "$image" Status) || fail "$target: Status is in the image"
    widths=$(body "$image" Widths) || fail "$target: Widths is in the image"
    polled=$(body "$image" Polled) || fail "$target: Polled is in the image"

    got=$(echo "$ring" | stores)
    if [ "$got" = "abbab" ]; then pass "$target: Ring stores memory, register, register, memory, register"; else fail "$target: Ring stores memory, register, register, memory, register (got $got)"; echo "$ring"; fi
    got=$(echo "$status" | loads)
    if [ "$got" = "aba" ]; then pass "$target: Status loads memory, register, memory"; else fail "$target: Status loads memory, register, memory (got $got)"; echo "$status"; fi
    # Five writes (the 64-bit one two stores on i386), and the unused 32-bit read among the reads.
    # (Not the frame's own: a value kept in a slot off the stack pointer.)
    wrote=$(echo "$widths" | grep -E '^mov [A-Z]+ PTR \[' | grep -cvE 'PTR \[[er](sp|bp)')
    wanted=4; [ $target = x86 ] && wanted=5
    if [ "$wrote" -eq $wanted ]; then pass "$target: Widths makes every write"; else fail "$target: Widths makes every write ($wrote of $wanted)"; echo "$widths"; fi
    # (i386 reads the 64-bit register as two 32-bit loads: the unused read is a third.)
    read32=$(echo "$widths" | grep -E '^(mov|test|cmp) [a-z0-9]+,DWORD PTR \[|^(test|cmp) DWORD PTR \[' | grep -cvE 'PTR \[[er](sp|bp)')
    wanted=1; [ $target = x86 ] && wanted=3
    if [ "$read32" -ge $wanted ]; then pass "$target: Widths makes the read nobody uses"; else fail "$target: Widths makes the read nobody uses ($read32 of $wanted 32-bit reads)"; echo "$widths"; fi
    store=$(echo "$polled" | grep -nE '^mov DWORD PTR \[' | head -1 | cut -d: -f1)
    load=$(echo "$polled" | grep -nE '^(mov|add) [a-z0-9]+,[A-Z]+ PTR \[' | tail -1 | cut -d: -f1)
    if [ -n "$store" ] && [ -n "$load" ] && [ "$load" -gt "$store" ]; then pass "$target: Polled loads memory after the register's write, in the loop"; else fail "$target: Polled loads memory after the register's write, in the loop (store at ${store:-none}, load at ${load:-none})"; echo "$polled"; fi
    flagged=$(body "$image" Flagged) || fail "$target: Flagged is in the image"
    compares=$(echo "$flagged" | grep -cE '^(cmp|test) [A-Z]+ PTR \[|^(cmp|test) [a-z0-9]+,[A-Z]+ PTR \[')
    if [ "$compares" -ge 2 ]; then pass "$target: Flagged compares the flag in memory on both sides of the read"; else fail "$target: Flagged compares the flag in memory on both sides of the read ($compares)"; echo "$flagged"; fi
    local=$(body "$image" Local) || fail "$target: Local is in the image"
    slotted=$(echo "$local" | grep -cE '^mov DWORD PTR \[[er](sp|bp)[^]]*\],')
    if [ "$slotted" -ge 2 ] && echo "$local" | grep -qE ',DWORD PTR \[[er](sp|bp)'; then pass "$target: Local writes its slot twice and reads it"; else fail "$target: Local writes its slot twice and reads it ($slotted stores)"; echo "$local"; fi
done

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
