#!/usr/bin/env bash
#
# MOVZX and MOVSX in the assembler.
#
#     tests/language/262_asm_movzx.sh
#
# The GUI kernel's 8-bit translating copy (a byte table applied four bytes at a
# time, as Windows' vSrcCopyS8D8) is hand-written assembly built on MOVZX, which
# `corc asm` did not know. Each form below is assembled flat and its bytes
# compared with the encoding binutils gives for it: byte and word sources,
# register and memory, base+index addressing, the 16-bit destination's 0x66
# prefix, and the sign-extending twins. Then the three refusals: a memory
# source that does not say its size, a byte destination, and a source as wide
# as the destination.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
# The compiler run.sh uses: CORC, else the newest build's corc.dll.
if [ -n "${CORC:-}" ]
then
    corc="$CORC"
else
    dll="$(ls -1t "$root"/compiler/bin/Release/net*/corc.dll 2>/dev/null | head -1)"
    corc="dotnet $dll"
fi
work="${WORK:-$(mktemp -d)}"

failures=0

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

cat > "$work/forms.asm" <<'ASM'
.bits 32
movzx eax, byte [esi]
movzx eax, bl
movzx eax, bx
movzx ax, bl
movsx ecx, byte [ebx + eax]
movsx edx, word [esi + 2]
movzx edx, word [esi]
movsx eax, cl
ASM

want="0f b6 06 0f b6 c3 0f b7 c3 66 0f b6 c3 0f be 0c 03 0f bf 56 02 0f b7 16 0f be c1"

if $corc asm --target x86-32 "$work/forms.asm" -o "$work/forms.bin" > "$work/forms.out" 2>&1
then
    got="$(od -An -tx1 -v "$work/forms.bin" | tr -s ' \n' ' ' | sed 's/^ //; s/ $//')"
    if [ "$got" = "$want" ]
    then
        pass "movzx/movsx encodings"
    else
        fail "movzx/movsx encodings: got '$got', wanted '$want'"
    fi
else
    fail "movzx/movsx assemble: $(cat "$work/forms.out")"
fi

refuse()
{
    printf '.bits 32\n%s\n' "$2" > "$work/bad.asm"
    if $corc asm --target x86-32 "$work/bad.asm" -o "$work/bad.bin" > "$work/bad.out" 2>&1
    then
        fail "$1: '$2' was accepted"
    elif grep -q -- "$3" "$work/bad.out"
    then
        pass "$1"
    else
        fail "$1: said '$(cat "$work/bad.out")', looked for '$3'"
    fi
}

refuse "movzx needs a sized memory source" "movzx eax, [esi]" "byte or a word"
refuse "movzx needs a wide destination" "movzx al, bl" "16-bit or 32-bit register"
refuse "movzx needs a narrower source" "movzx ax, word [esi]" "narrower"

[ -z "${WORK:-}" ] && rm -rf "$work"
exit $((failures > 0 ? 1 : 0))
