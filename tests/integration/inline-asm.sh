#!/bin/sh
# ASM BLOCKS ARE OPTIMISED, NOT CHAINS OF MOVES (docs/INLINE-ASSEMBLY.md), on
# i386 and x86-64: a block whose locals are all bound -- loaded by the head's
# `mov reg, local`, taken by the tail's `mov local, reg` -- compiles with no
# frame slot at all, its parameters loaded straight into the registers the
# block names and its results stored straight from them (Cpuid's four into
# the caller's buffer). A slot shows as a negative offset from the frame
# pointer.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/inline-asm.XXXXXX")

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

# A function's code by the start of its name, to the next symbol.
code() {
    at=$(nm "$1" | awk -v name="$2" 'index($3, name) == 1 { print $1; exit }')
    test -n "$at" || { printf 'FAIL no %s\n' "$2" >&2; exit 1; }
    objdump -d --no-show-raw-insn "$1" --start-address=0x$at | awk 'NR > 7 && /^ *[0-9a-f]+ </ { exit } NR > 7'
}

for target in x86 x86-64; do
    out="$work/bindings-$target"
    "$corc" compile --target "$target" tests/integration/inline-asm/bindings.cor -o "$out" 2> "$out.log" \
        || { cat "$out.log"; fail "bindings.cor did not compile for $target"; }
    "$out" > "$out.run" 2>&1 || { cat "$out.run"; fail "bindings.cor did not run on $target"; }
    grep -qx ok "$out.run" || { cat "$out.run"; fail "bindings.cor answered wrong on $target"; }
    frame=ebp; test "$target" = x86-64 && frame=rbp
    for f in m_Program_Add_ m_Program_Cpuid_; do
        code "$out" "$f" > "$out.$f.s"
        if grep -qE -- "-0x[0-9a-f]+\(%$frame\)" "$out.$f.s"; then
            cat "$out.$f.s"; fail "$f on $target passes a bound local through the frame"
        fi
    done
    grep -q cpuid "$out.m_Program_Cpuid_.s" || fail "Cpuid on $target has no CPUID"
done
printf 'PASS inline asm: %s\n' "$work"
