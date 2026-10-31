#!/bin/sh
# THE CALLER'S REGISTERS, WHERE THE COLLECTOR READS THEM (i386 and x86-64). A safepoint
# and the way into a collection push every callee-saved register in their
# prologues (Sys.SaveRegisters) without a system call; the functions a
# waiting frame calls to say it is stopped read that frame's EBX, ESI and
# EDI as it left them (Sys.CallerRegister: from the prologue's save slot, or
# the register itself); a method C calls is a thunk that gives none
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
# Each of the three read as the caller left it -- from its save slot, or the
# register itself -- and handed to GcThreads.BeginBlockingWith.
for f in m_Runtime_BeginBlocking_0 m_Runtime_EnterNative_0 m_GcThreads_BeginBlocking_0; do
    code "$f" > "$work/$f.s"
    grep -q 'call.*m_GcThreads_BeginBlockingWith_' "$work/$f.s" || { cat "$work/$f.s"; fail "$f does not call BeginBlockingWith"; }
    read=0
    for r in ebx esi edi; do
        if grep -qE "mov +%$r,%e" "$work/$f.s"; then read=$((read + 1)); continue; fi
        # Saved by the prologue: its push, and a load from below the frame pointer.
        if grep -qE "push +%$r" "$work/$f.s" && grep -qE 'mov +-0x[0-9a-f]+\(%ebp\),%e' "$work/$f.s"; then read=$((read + 1)); fi
    done
    test $read = 3 || { cat "$work/$f.s"; fail "$f reads $read of its caller's registers, not three"; }
done
# The thunk C enters by touches none of them.
thunk=$(nm "$work/701" | awk '$3 ~ /^m_Program_Compare_.*I32$/ { print $3; exit }')
test -n "$thunk" || fail "no thunk for Compare"
code "$thunk" > "$work/thunk.s"
# (The padding after it, `lea 0x0(%esi,%eiz,1),%esi`, changes nothing.)
if grep -vE 'lea +0x0\(%e(si|di)' "$work/thunk.s" | grep -qE '%e(bx|si|di)'; then cat "$work/thunk.s"; fail "the thunk gives a callee-saved register a value"; fi
"$work/701" > "$work/run.log" 2>&1 || { cat "$work/run.log"; fail "test 701 did not run"; }

# LONG MODE: the same, RBX and R12 to R15, the prologue's saves made by MOV.
"$corc" compile --target x86-64 tests/language/701_native_callbacks.cor -o "$work/701x" 2> "$work/compile64.log" \
    || { cat "$work/compile64.log"; fail "test 701 did not compile for x86-64"; }
code64() {
    at=$(nm "$work/701x" | awk -v name="$1" 'index($3, name) == 1 { print $1; exit }')
    test -n "$at" || { printf 'FAIL no %s\n' "$1" >&2; exit 1; }
    objdump -d --no-show-raw-insn "$work/701x" --start-address=0x$at | awk 'NR > 7 && /^ *[0-9a-f]+ </ { exit } NR > 7'
}
code64 m_Platform_SafePoint_ > "$work/safepoint64.s"
for r in rbx r12 r13 r14 r15; do
    grep -qE "mov +%$r,-0x[0-9a-f]+\(%rbp\)" "$work/safepoint64.s" || { cat "$work/safepoint64.s"; fail "Platform.SafePoint (x86-64) does not save $r"; }
done
if grep -q 'syscall' "$work/safepoint64.s"; then cat "$work/safepoint64.s"; fail "Platform.SafePoint (x86-64) makes a system call"; fi
for f in m_Runtime_BeginBlocking_0 m_Runtime_EnterNative_0; do
    code64 "$f" > "$work/$f-64.s"
    grep -q 'call.*m_GcThreads_BeginBlockingWith_' "$work/$f-64.s" || { cat "$work/$f-64.s"; fail "$f (x86-64) does not call BeginBlockingWith"; }
    for r in rbx r12 r13 r14 r15; do
        grep -qE "mov +(%$r,%r|-0x[0-9a-f]+\(%rbp\),%r)" "$work/$f-64.s" || { cat "$work/$f-64.s"; fail "$f (x86-64) does not read its caller's $r"; }
    done
done
"$work/701x" > "$work/run64.log" 2>&1 || { cat "$work/run64.log"; fail "test 701 did not run for x86-64"; }

printf 'PASS save registers: %s\n' "$work"
