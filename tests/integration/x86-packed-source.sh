#!/bin/sh
# Exercise automatic packed selection through ordinary C# lowering and linking.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/x86-packed-source.XXXXXX")
# Static runtime avoids the host glibc loader, which may require i686/CMOV.
# Both user code and runtime must be compiled for the selected CPU.
run_target()
{
    case "$profile" in
        486) timeout 30 qemu-i386 -cpu 486 "$1" ;;
        pentium) timeout 30 qemu-i386 -cpu pentium "$1" ;;
        pentium-mmx) timeout 30 qemu-i386 -cpu pentium,+mmx "$1" ;;
        k6*) timeout 30 qemu-i386 -cpu athlon "$1" ;;
        *) timeout 30 "$1" ;;
    esac
}
for profile in 486 pentium pentium-mmx excluded k6 k6-2 k6-3 k6-2+ k6-3+; do
    case "$profile" in
        excluded) set -- --cpu=pentium-mmx --disable-mmx ;;
        *) set -- "--cpu=$profile" ;;
    esac
    "$corc" compile "$@" --asm \
        tests/language/optimizer_packed_arrays.cor -o "$work/$profile" \
        > "$work/$profile.asm" 2> "$work/$profile.compile.log"
    case "$profile" in
        486|pentium|excluded)
            if grep -Eq '^[[:space:]]+(paddd|emms|femms)[[:space:]]*' "$work/$profile.asm"; then
                echo "Unexpected packed instructions for $profile" >&2; exit 1
            fi ;;
        *) grep -q 'paddd mm' "$work/$profile.asm"
           grep -q 'pcmpgtd mm' "$work/$profile.asm" ;;
    esac
    run_target "$work/$profile"
    printf 'PASS ordinary source selection and execution: %s\n' "$profile"
    "$corc" compile "$@" tests/language/runtime_compare_bytes.cor -o "$work/runtime-$profile" \
        > "$work/runtime-$profile.compile.log" 2>&1
    run_target "$work/runtime-$profile"
    printf 'PASS runtime comparison alignments, tails and unsigned order: %s\n' "$profile"
done
printf 'Evidence: %s\n' "$work"
