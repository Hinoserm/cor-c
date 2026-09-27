#!/usr/bin/env bash
# Language tests compiled as separate units: each test alone against the
# library's declarations, linked with a library object built once (see
# separate/corc-unit). What a single-program compile never shows -- a
# definition both objects carry, a layout the two describe differently, an
# async body the archive cannot hold, a constructor only one unit makes, an
# overload chosen only because a second binding round saw the call again.
#
#   tests/integration/separate-units.sh           the regression set below
#   tests/integration/separate-units.sh all       every language test
#   tests/integration/separate-units.sh NAME ...  the named tests
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p build
work="$(mktemp -d "$root/build/separate-units.XXXXXX")"
fail() { echo "$1; diagnostics: $work" >&2; exit 1; }
anchor=tests/integration/separate/Anchor.cor

$corc library-sources | while IFS= read -r lib; do realpath -m "$lib"; done > "$work/libs.txt"
$corc index --assembly Suite "$anchor" -o "$work/library.idx" > "$work/index.log" 2>&1 || fail "library index failed"
$corc compile --lib "$anchor" --obj -o "$work/library.o" > "$work/library.log" 2>&1 || fail "library object failed"

tests=("$@")
if [ ${#tests[@]} = 0 ]; then
    tests=(
        # one definition in two objects, each certified the same
        412_linq_grouping 505_boxing 519_switch_expression_patterns 633_typed_equals_is_guarded
        case_unchecked_constant capture_binder_locals generic_local_function optimizer_boxed_float_equality
        # async bodies through the IR archive
        80_async_basic 90_async_generic 93_async_gc
        # managed layouts the two objects agree on
        18_classes record_equality struct_inline_fields
        # a constructor only the canonical copy of a generic has
        502_program_shadows_library 504_generic_indexer_type
        # the library's shared copies: interface members every dictionary implements
        682_readonly_dictionary_views
        # the overload completed from defaults, bound once
        608_overload_by_word
        # lifetimes across units
        670_fresh_return 671_owned_fields 674_owned_variables
    )
fi
[ "${tests[*]}" = all ] && tests=("")

export SEPARATE_CORC="$corc" SEPARATE_LIBS="$work/libs.txt" SEPARATE_INDEX="$work/library.idx" SEPARATE_LIB_OBJ="$work/library.o"
failed=()
for name in "${tests[@]}"; do
    CORC="$root/tests/integration/separate/corc-unit" CORC_LIBS="$(tr '\n' ' ' < "$work/libs.txt")" \
        bash tests/language/run.sh ${name:+"$name"} > "$work/run-${name:-all}.log" 2>&1 || failed+=("${name:-all}")
done
[ ${#failed[@]} = 0 ] || { grep -h '^FAIL' "$work"/run-*.log >&2; fail "separate units: ${failed[*]}"; }
echo "PASS separate units: ${#tests[@]} test run(s), each its own unit linked with the library object"
