#!/usr/bin/env bash
#
# The half of test 2337 its output cannot show: the listing --asm prints,
# both targets. It is the code as it will be encoded, in each mode's own
# words: no virtual register, no name of the machine IR's where the
# processor's instruction has another, each mode's registers and frames.
#
#     tests/language/2337_assembly_listing_both_modes.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/listing.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

# A function's lines in the listing: from its label to the blank line after it.
body() { awk -v n="m_Program_$2_" 'index($0, n) == 1 && /:$/ { on = 1; next } on && /^$/ { exit } on { sub(/^ +/, ""); print }' "$1"; }
has() { if echo "$3" | grep -qE "$4"; then pass "$1: $2"; else fail "$1: $2"; echo "$3"; fi; }

for target in x86 x86-64; do
    flags="--cpu=athlon64"; [ $target = x86 ] && flags="$flags --disable-3dnow"
    listing="$work/listing-$target.s"
    if $corc compile "$here/2337_assembly_listing_both_modes.cor" --asm --target $target $flags > "$listing" 2> "$listing.err" && [ -s "$listing" ]
    then pass "$target: the listing is printed"
    else fail "$target: the listing is printed"; head -5 "$listing.err"; continue
    fi
    bits=32; [ $target = x86-64 ] && bits=64
    if grep -q "^bits $bits\$" "$listing"; then pass "$target: it says bits $bits"; else fail "$target: it says bits $bits"; fi
    # Every instruction line (eight spaces in): no virtual register, and a
    # mnemonic the processor has -- none of the machine IR's own names.
    code=$(grep -E '^        [a-z;.]' "$listing")
    if echo "$code" | grep -qE '(^|[ ,\[+*-])v[0-9]+($|[] ,+*-])'; then fail "$target: no virtual register is left"; echo "$code" | grep -E '(^|[ ,\[+*-])v[0-9]+($|[] ,+*-])' | head -5; else pass "$target: no virtual register is left"; fi
    own=$(echo "$code" | awk '{ print $1 }' | grep -xE 'movf|movgx|addf|subf|mulf|divf|sqrtf|xorf|ucomif|cvtftof|cvtinttof|cvtftoint|mulwide|imulwide|cwd|imul3|repmovsb|repmovsd|repmovsq|repstosb|repstosd|repstosq|prologue|epilogue|softint|jmptable|jmpind|callind|callsafe|callkeep[a-z]*|loadcs|loadsegments|getgs|setgs|readcs|readflags|compilerfence|saveregs|callerreg|movdtox|movdfromx|xmmopv|xmmopvdef|gotpc|gsself' | sort -u | tr '\n' ' ')
    if [ -z "$own" ]; then pass "$target: every mnemonic is the processor's"; else fail "$target: every mnemonic is the processor's (found: $own)"; fi

    framed=$(body "$listing" Framed)
    widen=$(body "$listing" Widen)
    divide=$(body "$listing" Divide)
    halve=$(body "$listing" Halve)
    whole=$(body "$listing" Whole)
    put=$(body "$listing" Put)
    if [ $target = x86-64 ]; then
        has $target "Framed's way in and out are long mode's" "$framed" '^push rbp$'
        has $target "Framed sets its base" "$framed" '^mov rbp, rsp$'
        has $target "Framed leaves" "$framed" '^(leave|pop rbp)$'
        has $target "Framed calls Put by name" "$framed" '^call m_Program_Put_'
        has $target "Widen is MOVSXD into a 64-bit register" "$widen" '^movsxd r[a-z0-9]+, e[a-z]+$|^movsxd r[a-z0-9]+, r[0-9]+d$'
        has $target "Divide spreads the sign by CQO and divides a 64-bit register" "$divide" '^cqo$'
        has $target "Divide's IDIV names a 64-bit register" "$divide" '^idiv r[a-z0-9]+$'
        has $target "Halve divides in an XMM register" "$halve" '^divsd xmm[0-9]+, '
        has $target "Whole truncates by CVTTSD2SI" "$whole" '^cvttsd2si e[a-z]+, xmm[0-9]+$'
        has $target "Put stores a byte from a byte register" "$put" '^mov (byte )?\[[^]]+\], ([a-d]l|[sd]il|r[0-9]+b)$'
        if echo "$code" | grep -qE '(^|[ ,\[])e(sp|bp)($|[] ,+-])'; then fail "$target: no frame is named by a 32-bit register"; else pass "$target: no frame is named by a 32-bit register"; fi
    else
        has $target "Framed's way in is i386's" "$framed" '^push ebp$'
        has $target "Framed sets its base" "$framed" '^mov ebp, esp$'
        has $target "Framed calls Put by name" "$framed" '^call m_Program_Put_'
        has $target "Put stores a byte from a byte register" "$put" '^mov (byte )?\[[^]]+\], [a-d]l$'
        if echo "$code" | grep -qE '(^|[ ,\[])r([a-ds][xpi]|[0-9]+[dwb]?)($|[] ,+-])'; then fail "$target: no 64-bit register is named"; else pass "$target: no 64-bit register is named"; fi
    fi
done

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
