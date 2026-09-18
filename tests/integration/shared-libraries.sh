#!/usr/bin/env bash
#
# THE SAME TESTS, LINKED AGAINST THE SHARED OBJECTS.
#
#   tests/tools/shared-libraries.sh          build build/lib and run them all
#   tests/tools/shared-libraries.sh 621      run the one whose name matches
#
# tests/language/run.sh compiles every test with the class library's source
# compiled into it. These are the tests numbered 620 and up, compiled
# `--dynamic` instead: the runtime and the class library are the shared
# objects in build/lib, the system's ld-linux.so.2 links them at load, and
# the answers must be the same to the byte.
#
# It also checks the ELF the compiler emits against what the CORSAC kernel's
# loader is written to read -- see "The ELF contract" in
# docs/X86-BACKEND.md. Those checks are the reason this is a script
# and not another .cor file.

set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/Release/net10.0/corc}"
filter="${1:-}"
mkdir -p "$root/build"
work="$(mktemp -d "$root/build/shared.XXXXXX")"
echo "Shared-library acceptance; diagnostics retained: $work"

passed=0
failed=0
failed_names=""

ok() { passed=$((passed + 1)); printf 'PASS %s\n' "$1"; }
bad() { failed=$((failed + 1)); failed_names="$failed_names $1"; printf 'FAIL %s (%s)\n' "$1" "$2"; }

if ! bash "$root/tests/integration/build-libraries.sh" > "$work/libs.log" 2>&1; then
    sed 's/^/    /' "$work/libs.log"
    echo "the shared libraries did not build" >&2
    exit 2
fi

# ---- the ELF contract -------------------------------------------------------

check_image() {
    local file="$1" name="$2" kind="$3"

    local dyn; dyn="$(readelf -dW "$file" 2>/dev/null)"
    local rel; rel="$(readelf -rW "$file" 2>/dev/null)"
    local hdr; hdr="$(readelf -hlW "$file" 2>/dev/null)"

    if grep -q TEXTREL <<< "$dyn"; then
        bad "$name" "DT_TEXTREL: the text segment cannot be shared"
    else
        ok "$name text has no relocations"
    fi

    local kinds; kinds="$(awk 'NR > 3 && $3 ~ /^R_386_/ { print $3 }' <<< "$rel" | sort -u | tr '\n' ' ')"
    local bad_kind=0 one
    for one in $kinds; do
        case "$one" in
            R_386_RELATIVE|R_386_32|R_386_PC32|R_386_GLOB_DAT|R_386_JUMP_SLOT) ;;
            *) bad_kind=1; bad "$name" "relocation $one is not one the loader accepts" ;;
        esac
    done
    [ "$bad_kind" = 0 ] && ok "$name uses only the five relocation types"

    if grep -q "R_386_COPY" <<< "$rel"; then
        bad "$name" "a copy relocation: a type's identity would be two addresses"
    else
        ok "$name has no copy relocation"
    fi

    if grep -q "(HASH)" <<< "$dyn"; then
        ok "$name has DT_HASH"
    else
        bad "$name" "no DT_HASH, and GNU hash alone is not read"
    fi

    if grep -q "BIND_NOW" <<< "$dyn"; then
        ok "$name binds eagerly"
    else
        bad "$name" "no DT_BIND_NOW"
    fi

    case "$kind" in
        lib)
            grep -q "(SONAME)" <<< "$dyn" && ok "$name has a SONAME" || bad "$name" "no DT_SONAME"
            grep -q "(INIT)" <<< "$dyn" && ok "$name has DT_INIT" || bad "$name" "no DT_INIT"
            grep -qE "^ *Type: *DYN" <<< "$hdr" && ok "$name is ET_DYN" || bad "$name" "not ET_DYN"
            ;;
        exe)
            grep -q "/lib/ld-linux.so.2" <<< "$hdr" && ok "$name names the interpreter" \
                || bad "$name" "no PT_INTERP=/lib/ld-linux.so.2"
            grep -q "(DEBUG)" <<< "$dyn" && ok "$name has DT_DEBUG for the link map" \
                || bad "$name" "no DT_DEBUG: a loader that cannot call into the program has nowhere to put the link map"
            ;;
    esac
}

if [ -z "$filter" ]; then
    check_image "$root/build/lib/libcorsacrt.so" "libcorsacrt.so" lib
    check_image "$root/build/lib/libSystem.IO.so" "libSystem.IO.so" lib
fi

# ---- the tests --------------------------------------------------------------

expected_output() {
    awk '
        /^\/\/ expect-output:/ { on = 1; next }
        on && /^\/\/ / { print substr($0, 4); next }
        on { exit }
    ' "$1"
}

first=1
for source in "$root"/tests/language/6[2-9][0-9]_shared_*.cor "$root/tests/language/514_stack_traces.cor"; do
    [ -e "$source" ] || continue
    name="$(basename "$source" .cor)"
    if [ -n "$filter" ] && [[ "$name" != *"$filter"* ]]; then
        continue
    fi

    exe="$work/$name"
    if ! "$corc" compile --dynamic "$source" -o "$exe" > "$work/$name.cc" 2>&1; then
        sed 's/^/    /' "$work/$name.cc" | head -20
        bad "$name" "did not compile"
        continue
    fi

    if [ "$first" = 1 ]; then
        check_image "$exe" "$name" exe
        first=0
    fi

    # NOTHING OF THE CLASS LIBRARY IS IN IT. The collector is the clearest
    # witness: a statically linked program has all of Gc's code in it, and a
    # dynamically linked one has none and imports what it calls.
    mine="$(readelf -sW "$exe" | awk '$7 != "UND" && $4 == "FUNC" { print $8 }' | grep -c "^m_Gc_" || true)"
    theirs="$(readelf -sW --dyn-syms "$exe" | awk '$7 == "UND" { print $8 }' | grep -c "^m_" || true)"
    if [ "$mine" = 0 ] && [ "$theirs" -gt 0 ]; then
        ok "$name carries no collector and imports $theirs names"
    else
        bad "$name" "$mine of the collector's functions are compiled into it"
    fi

    expected_output "$source" > "$work/$name.want"
    if ! timeout --foreground -k 2 20 "$exe" > "$work/$name.out" 2> "$work/$name.err"; then
        sed 's/^/    /' "$work/$name.err" | head -10
        bad "$name" "exited non-zero"
        continue
    fi
    if cmp -s "$work/$name.want" "$work/$name.out"; then
        ok "$name"
    else
        diff -u --label expected --label actual "$work/$name.want" "$work/$name.out" | sed 's/^/    /'
        bad "$name" "stdout differs"
    fi
done

echo
echo "$passed passed, $failed failed"
if [ "$failed" -ne 0 ]; then
    echo "failed:$failed_names"
    exit 1
fi
exit 0
