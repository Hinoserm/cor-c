#!/usr/bin/env bash
#
# A function's stub -- its own name, where its stack arguments are loaded
# into registers -- ends where its body begins, with no jump between them.
#
#     tests/language/2629_register_stub_runs_into_body.sh
#
# The half of test 2629 its output cannot show: the image itself. Of every
# function with a register entry (NAME and NAME$r both in the image, the x86
# backend's RegisterArgs), where the entry is the body: the body begins 4, 8
# or 12 bytes after the name -- one load of four bytes an argument and
# nothing else -- and at the function alignment, 16 for the K8 this is
# compiled for; and the bytes under the name are those loads, `mov eax,
# [esp+4]` first. (Not a function whose body keeps its arguments on the
# stack, an asm block in it naming the stack: there the body has the name,
# the entry is a stub after it that pushes them, and the image has NAME$s.)
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/register-stub.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

if $corc compile "$here/2629_register_stub_runs_into_body.cor" -o "$work/stub" --cpu=athlon64 --disable-3dnow > "$work/stub.log" 2>&1
then pass "the program compiles"
else fail "the program compiles"; cat "$work/stub.log"; exit 1
fi

# NAME ADDRESS for every code symbol; then each NAME$r beside its NAME.
nm "$work/stub" | awk '$2 ~ /^[tT]$/ {print $3, $1}' > "$work/symbols"
awk '
    { at[$1] = strtonum("0x" $2) }
    END {
        for (name in at) {
            if (name !~ /\$r$/) continue
            own = substr(name, 1, length(name) - 2)
            if (!(own in at) || (own "$s") in at) continue
            gap = at[name] - at[own]
            total++
            if (gap != 4 && gap != 8 && gap != 12) { bad++; print "gap " gap ": " own > "/dev/stderr" }
            else if (at[name] % 16 != 0) { unaligned++; print "body not aligned: " own > "/dev/stderr" }
        }
        print total + 0, bad + 0, unaligned + 0
    }' "$work/symbols" > "$work/counts" 2> "$work/wrong"
read -r total bad unaligned < "$work/counts"
if [ "$total" -ge 20 ]; then pass "the image has functions with a register entry ($total)"
else fail "the image has functions with a register entry ($total)"; fi
if [ "$bad" -eq 0 ]; then pass "each body begins 4, 8 or 12 bytes after its function's name"
else fail "each body begins 4, 8 or 12 bytes after its function's name ($bad of $total do not)"; head -5 "$work/wrong"; fi
if [ "$unaligned" -eq 0 ]; then pass "each body is at the function alignment"
else fail "each body is at the function alignment ($unaligned of $total are not)"; head -5 "$work/wrong"; fi

# The bytes under the name, up to the body: of one function of each size
# (the first by name with one, two and three arguments).
for pair in "4 8b 44 24 04" "8 8b 44 24 04 8b 54 24 08" "12 8b 44 24 04 8b 54 24 08 8b 4c 24 0c"; do
    set -- $pair; gap=$1; shift; want="$*"
    own=$(sort "$work/symbols" | awk -v gap="$gap" '
        { at[$1] = strtonum("0x" $2); order[n++] = $1 }
        END { for (i = 0; i < n; i++) { name = order[i]; if ((name "$r") in at && at[name "$r"] - at[name] == gap) { print name; exit } } }')
    if [ -z "$own" ]; then fail "a function whose stub is $gap bytes is in the image"; continue; fi
    from=$(awk -v n="$own" '$1 == n {print $2}' "$work/symbols")
    to=$(awk -v n="$own\$r" '$1 == n {print $2}' "$work/symbols")
    got=$(objdump -s --start-address=0x$from --stop-address=0x$to "$work/stub" | awk '/^ [0-9a-f]+ /{for (i = 2; i <= 5; i++) if ($i ~ /^[0-9a-f]+$/) printf "%s", $i}' | sed 's/../& /g; s/ $//')
    if [ "$got" = "$want" ]; then pass "a stub of $gap bytes is its loads and nothing else ($got)"
    else fail "a stub of $gap bytes is its loads and nothing else ($own: got '$got', want '$want')"; fi
done

if [ "$failures" -eq 0 ]; then echo "PASS 2629_register_stub_runs_into_body"; exit 0; fi
echo "FAIL 2629_register_stub_runs_into_body ($failures)"; exit 1
