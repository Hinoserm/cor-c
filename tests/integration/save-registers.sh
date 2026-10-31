#!/bin/sh
# THE CALLER'S REGISTERS, WHERE THE COLLECTOR READS THEM (i386). A safepoint
# and the way into a collection push every callee-saved register in their
# prologues (Sys.SaveRegisters) without a system call; the functions a
# waiting frame calls to say it is stopped write that frame's EBX, ESI and
# EDI down as it left them (Sys.SpillRegisters: from the prologue's save
# slot, or the register itself); a method C calls is a thunk that gives none
# of them a value, so what it hands on is C's. Test 701 calls into C, and C
# calls back.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/save-registers.XXXXXX")

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

"$corc" compile tests/language/701_native_callbacks.cor -o "$work/701" 2> "$work/compile.log" \
    || { cat "$work/compile.log"; fail "test 701 did not compile"; }
# A function's code by the start of its name, to the next symbol.
code() {
    at=$(nm "$work/701" | awk -v name="$1" 'index($3, name) == 1 { print $1; exit }')
    test -n "$at" || { printf 'FAIL no %s\n' "$1" >&2; exit 1; }
    objdump -d --no-show-raw-insn "$work/701" --start-address=0x$at | awk 'NR > 7 && /^[0-9a-f]+ </ { exit } NR > 7'
}
# The pushes in the prologue, its first six instructions. EnterCollection is
# put in place in GcRoots.Enter, its pushes with it, and the collection with
# them, whose system calls are its own.
for f in m_Platform_SafePoint_ m_GcRoots_Enter_; do
    code "$f" | head -6 > "$work/$f.s"
    for r in ebx esi edi; do
        grep -q "push *%$r" "$work/$f.s" || { cat "$work/$f.s"; fail "$f does not push $r"; }
    done
done
code m_Platform_SafePoint_ > "$work/safepoint.s"
if grep -q 'int  *\$0x80' "$work/safepoint.s"; then cat "$work/safepoint.s"; fail "Platform.SafePoint makes a system call"; fi
# Each of the three written down once: a save slot copied (push, pop) or the
# register stored.
for f in m_Runtime_BeginBlocking_0 m_Runtime_EnterNative_0 m_GcThreads_BeginBlocking_0; do
    code "$f" > "$work/$f.s"
    copied=$(grep -c 'pop  *-*0*x*[0-9a-f]*(%e' "$work/$f.s" || true)
    stored=$(grep -cE 'mov  *%e(bx|si|di),(0x[0-9a-f]+)?\(%e' "$work/$f.s" || true)
    test $((copied + stored)) -ge 3 || { cat "$work/$f.s"; fail "$f writes down $((copied + stored)) registers, not three"; }
done
# The thunk C enters by touches none of them.
thunk=$(nm "$work/701" | awk '$3 ~ /^m_Program_Compare_.*I32$/ { print $3; exit }')
test -n "$thunk" || fail "no thunk for Compare"
code "$thunk" > "$work/thunk.s"
# (The padding after it, `lea 0x0(%esi,%eiz,1),%esi`, changes nothing.)
if grep -vE 'lea +0x0\(%e(si|di)' "$work/thunk.s" | grep -qE '%e(bx|si|di)'; then cat "$work/thunk.s"; fail "the thunk gives a callee-saved register a value"; fi
"$work/701" > "$work/run.log" 2>&1 || { cat "$work/run.log"; fail "test 701 did not run"; }

printf 'PASS save registers: %s\n' "$work"
