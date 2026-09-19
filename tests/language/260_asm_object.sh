#!/usr/bin/env bash
#
# An assembled object links into a COR-C# program.
#
#     tests/lang/260_asm_object.sh
#
# `corc build --obj` turns assembly into a relocatable i386 object -- sections,
# a symbol for every .global label, a relocation for every reference the linker
# has to finish -- and `corc compile --with` links such objects in. That is what
# lets the x86 kernel have a real entry stub and real interrupt vectors instead
# of instructions written into memory a byte at a time at boot.
#
# What is proved here, on Linux, where a failure is an exit code rather than a
# triple fault: the object binutils reads is the object we meant; the four
# sections and both relocation kinds survive the link; assembly can write to a
# COR-C# static by its mangled name (which is how the kernel's entry stub hands
# the loader's BootInfo pointer over); and --asm-entry lets the assembly own
# _start and call the COR-C# entry stub by name.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="$root/compiler/bin/managed/Release/net10.0/corc"
work="${WORK:-$(mktemp -d)}"
libs="lib/std.cor lib/rt/runtime.cor lib/rt/gc.cor lib/threading.cor lib/threading-linux.cor lib/sys/linux.cor lib/io.cor lib/console.cor lib/environment.cor lib/net.cor lib/security.cor lib/signals.cor lib/process.cor"

failures=0

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

check()
{
    if [ "$1" = 0 ]
    then
        pass "$2"
    else
        fail "$2"
    fi
}

wants()
{
    if printf '%s' "$2" | grep -q -- "$3"
    then
        pass "$1"
    else
        fail "$1 (looked for '$3')"
    fi
}

cat > "$work/parts.asm" <<'ASM'
; Four sections, both relocation kinds, and a write to a COR-C# static.
.bits 32

.section .text
.global _start
.global helper
.extern corc_start
.extern s_Marker_Value

_start:
    cld
    mov eax, pointer            ; .text -> .data, absolute
    mov eax, [eax]              ; the pointer .data holds: .data -> .rodata
    mov eax, [eax]              ; the answer itself
    mov [s_Marker_Value], eax   ; a COR-C# static, by its mangled name
    mov dword [s_Marker_Value + 4], 0
    call helper                 ; same section: a displacement, not a relocation
    jmp corc_start              ; another object: R_386_PC32

helper:
    mov dword [counter], 1      ; .text -> .bss, absolute
    ret

.section .data
pointer:
    .dd answer                  ; .data -> .rodata: R_386_32

.section .rodata
answer:
    .dd 42

.section .bss
counter:
    .space 8
ASM

cat > "$work/marker.cor" <<'COR'
// The static the assembly writes to: Lowering mangles it s_Marker_Value.
class Marker
{
    public static long Value;
}

static class Program
{
    static int Main()
    {
        return (int)Marker.Value;
    }
}
COR

"$corc" build --target x86-32 "$work/parts.asm" --obj -o "$work/parts.o" 2>"$work/build.err"
check $? "corc build --obj assembles to an object"
if [ -s "$work/build.err" ]
then
    sed 's/^/    /' "$work/build.err"
fi

if [ ! -f "$work/parts.o" ]
then
    echo "260_asm_object: nothing to test; the assembly did not build"
    exit 1
fi

headers="$(readelf -hSW "$work/parts.o" 2>&1)"
wants "it is an ET_REL i386 object"      "$headers" "REL (Relocatable file)"
wants "for Intel 80386"                  "$headers" "Intel 80386"
wants "it has a .text section"           "$headers" ".text"
wants "it has a .rodata section"         "$headers" ".rodata"
wants "it has a .data section"           "$headers" ".data"
wants "it has a NOBITS .bss section"     "$headers" "NOBITS"

symbols="$(readelf -sW "$work/parts.o" 2>&1)"
wants ".global exports _start"               "$symbols" "GLOBAL DEFAULT .*1 _start"
wants ".global exports helper"               "$symbols" "GLOBAL DEFAULT .*1 helper"
wants ".extern leaves corc_start undefined"  "$symbols" "UND corc_start"
wants "the COR-C# static is undefined too"   "$symbols" "UND s_Marker_Value"

relocs="$(readelf -rW "$work/parts.o" 2>&1)"
wants "an absolute relocation on the static" "$relocs" "R_386_32 .*s_Marker_Value"
wants "a call relocation on the entry stub"  "$relocs" "R_386_PC32 .*corc_start"
wants ".data is relocated against .rodata"   "$relocs" "R_386_32 .*\.rodata"
wants "a reference into .bss"                "$relocs" "R_386_32 .*\.bss"

code="$(objdump -dr --section=.text "$work/parts.o" 2>&1)"
check $? "objdump disassembles the object"
wants "the same-section call needs no relocation" \
      "$(printf '%s' "$code" | grep -A1 'call')" "call"

sources=""
for lib in $libs
do
    sources="$sources $root/$lib"
done

"$corc" compile $sources "$work/marker.cor" --with "$work/parts.o" \
    --asm-entry corc_start --entry _start -o "$work/marker" 2>"$work/link.err"
check $? "corc compile --with links the object in"
if [ -s "$work/link.err" ]
then
    sed 's/^/    /' "$work/link.err"
fi

if [ -x "$work/marker" ]
then
    "$work/marker"
    got=$?
    if [ "$got" = 42 ]
    then
        pass "the assembly ran, followed both pointers, and told COR-C# the answer"
    else
        fail "the linked program exits 42 (got $got)"
    fi

    entry="$(readelf -hW "$work/marker" | sed -n 's/.*Entry point address: *//p')"
    start="0x$(readelf -sW "$work/marker" | awk '$8 == "_start" { print $2 }' | head -1)"
    if [ "$((entry))" = "$((start))" ]
    then
        pass "--entry _start points the ELF at the assembly"
    else
        fail "--entry _start points the ELF at the assembly ($entry vs $start)"
    fi
else
    fail "the linked program exists"
fi

if [ "$failures" = 0 ]
then
    echo "260_asm_object: all checks passed"
    exit 0
fi

echo "260_asm_object: $failures check(s) failed; work in $work"
exit 1
