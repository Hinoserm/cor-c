#!/bin/sh
# Exercise automatic packed selection through ordinary C# lowering and linking.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/x86-packed-source.XXXXXX")
export LD_LIBRARY_PATH="$root/build/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
for profile in 486 pentium pentium-mmx excluded k6-2 k6-3+; do
    case "$profile" in
        excluded) set -- --cpu=pentium-mmx --disable-mmx ;;
        *) set -- "--cpu=$profile" ;;
    esac
    "$corc" compile "$@" --dynamic --libdir "$root/build/lib" --asm \
        tests/language/optimizer_packed_arrays.cor -o "$work/$profile" \
        > "$work/$profile.asm" 2> "$work/$profile.compile.log"
    case "$profile" in
        486|pentium|excluded)
            if grep -Eq '^[[:space:]]+(paddd|emms|femms)[[:space:]]*' "$work/$profile.asm"; then
                echo "Unexpected packed instructions for $profile" >&2; exit 1
            fi ;;
        *) grep -q 'paddd mm' "$work/$profile.asm" ;;
    esac
    case "$profile" in
        pentium) qemu-i386 -cpu pentium "$work/$profile" ;;
        pentium-mmx) qemu-i386 -cpu pentium,+mmx "$work/$profile" ;;
        k6-*) qemu-i386 -cpu athlon "$work/$profile" ;;
        *) "$work/$profile" ;;
    esac
    printf 'PASS ordinary source selection and execution: %s\n' "$profile"
done
printf 'Evidence: %s\n' "$work"
