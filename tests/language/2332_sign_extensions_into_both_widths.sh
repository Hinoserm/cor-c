#!/usr/bin/env bash
#
# The half of test 2332 its output cannot show: long mode's image. A byte's
# or a word's sign extension into a long is ONE instruction through the
# whole register (Peephole.SignExtendOnce), from a register and from
# memory; an int's is the 32-bit form, with no move after it to clear the
# upper half again.
#
#   WideFromByte, WideFromShort   movsx r64, r8/r16 and no movsxd
#   WideLoad8                     movsx r64, BYTE PTR and no movsxd after it
#   FromByte, Load8               movsx r32, ... and nothing of 64 bits
#   Unsigned                      movsx r32, r8 and no 32-bit move onto itself
#
#     tests/language/2332_sign_extensions_into_both_widths.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/sign-extensions.XXXXXX")}"

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
# Whether the body holds exactly one sign extension, of the given shape, and no second step.
once() {
    local target=$1 name=$2 shape=$3 what=$4 code
    code=$(body "$image" "$name") || { fail "$target: $name is in the image"; return; }
    # (A second step would follow the extension; an array's index has a
    # MOVSXD of its own before it, to the address's width.)
    if [ "$(echo "$code" | grep -cE '^movsx ')" -eq 1 ] && echo "$code" | grep -qE "^movsx $shape" \
        && ! echo "$code" | sed -n '/^movsx /,$p' | grep -qE '^(movsxd|cdqe)'
    then pass "$target: $name $what"
    else fail "$target: $name $what"; echo "$code"
    fi
}

target=x86-64
image="$work/sign-$target"
if $corc compile "$here/2332_sign_extensions_into_both_widths.cor" -o "$image" --target $target --cpu=athlon64 > "$image.log" 2>&1
then pass "$target: the program compiles"
else fail "$target: the program compiles"; cat "$image.log"; echo; echo "$failures failed"; exit 1
fi
# (objdump -M intel: 64-bit registers r.., 32-bit e.. or r..d, bytes .l/..b, words two letters or r..w.)
r64='r[a-z0-9]+'; r32='(e[a-z]+|r[0-9]+d)'
once $target WideFromByte "$r64,[a-z0-9]+l\$|^movsx $r64,r[0-9]+b\$" "extends a byte to a long in one instruction"
once $target WideFromShort "$r64,([a-z]{2}|r[0-9]+w)\$" "extends a word to a long in one instruction"
once $target WideLoad8 "$r64,BYTE PTR" "extends a byte of memory to a long in one instruction"
once $target FromByte "$r32,[a-z0-9]+[lb]\$" "extends a byte to an int by the 32-bit form"
once $target Load8 "$r32,BYTE PTR" "extends a byte of memory to an int by the 32-bit form"
code=$(body "$image" Unsigned) || fail "$target: Unsigned is in the image"
if echo "$code" | grep -qE "^movsx $r32," && ! echo "$code" | grep -qE '^mov (e[a-z]+|r[0-9]+d),(e[a-z]+|r[0-9]+d)$'
then pass "$target: Unsigned clears the upper half by the extension itself"
else fail "$target: Unsigned clears the upper half by the extension itself"; echo "$code"
fi

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
