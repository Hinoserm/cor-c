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
        # owned elements through accessors, a frame object's fields freed at
        # the link, and `??=` fills that replace nothing
        1180_owned_elements_through_accessors 1181_owned_elements_through_accessors_refused
        1182_owned_field_filled_when_empty 1494_fill_store_window
        # regions, which only a closed link finds: a loop's region left on
        # every way out, and a lap's carried chain on the heap
        1070_region_loop_laps
        # a shared generic's face (Func<__canon>, IMaker<__canon>) reaches every instantiation's methods
        1518_shared_generic_lazy_factory_keeps_what_it_stores 1519_shared_generic_maker_variants_keep_what_they_make
        1520_shared_generic_func_keeps_what_it_makes 1521_shared_generic_action_keeps_what_it_is_handed
        1522_shared_generic_lazy_value_keeps_its_sink 1524_shared_generic_factory_across_units
        1525_shared_generic_relay_across_units 1526_shared_generic_interface_maker_keeps_what_it_makes
        # a frame object's frees where each lap of a region ends, before the lap is given back
        1514_lap_region_frame_list_freed_where_the_lap_ends 1515_lap_region_frame_fields_freed_where_the_lap_ends
        1516_lap_region_frame_builder_freed_where_the_lap_ends 1517_lap_region_frame_freed_on_every_way_out
        1527_lap_region_frame_freed_where_the_loop_ends 1528_lap_region_frame_freed_where_a_break_leaves
        # no collector, which only a closed link can say of units (HeapDecision)
        63_no_heap 762_gcless_static_tables 767_gcless_run_once 768_gcless_latched_once
        # a collector where unwinds are unbounded or a held field leaks, none where neither
        1503_trap_path_exception_kept 1506_owned_array_across_bounds_throw 1508_field_replaced_in_long_lived_owner
        1509_field_cleared_in_long_lived_owner 1510_one_exception_thrown_round_a_loop 1513_gcless_caught_once
        # what a catch may free, over the throws of what the program reaches
        1483_catch_keeps_and_rethrows 1484_filter_keeps_and_declines 1485_thrower_reads_what_its_catch_took
        1486_rethrow_caught_again_then_reused 1489_rethrow_only_and_finally_free_at_catch
        # a field a library function refuses, owned where the program never runs it
        764_gcless_caught_exceptions 1495_gcless_caught_exception_message_across_units
        1496_gcless_dropped_exception_message_across_units 1500_exception_message_kept_through_a_virtual_call_across_units
    )
fi
[ "${tests[*]}" = all ] && tests=("")

export SEPARATE_CORC="$corc" SEPARATE_INDEX="$work/library.idx" SEPARATE_LIB_OBJ="$work/library.o"
failed=()
for name in "${tests[@]}"; do
    # Its work under this run's own directory, never the system's /tmp.
    TMPDIR="$work" CORC="$root/tests/integration/separate/corc-unit" CORC_LIBS="$(tr '\n' ' ' < "$work/libs.txt")" \
        bash tests/language/run.sh ${name:+"$name"} > "$work/run-${name:-all}.log" 2>&1 || failed+=("${name:-all}")
done
[ ${#failed[@]} = 0 ] || { grep -h '^FAIL' "$work"/run-*.log >&2; fail "separate units: ${failed[*]}"; }
echo "PASS separate units: ${#tests[@]} test run(s), each its own unit linked with the library object"
