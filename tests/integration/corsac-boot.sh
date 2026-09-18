#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
os="${CORSAC_ROOT:-$root/../corsac-pc}"
corc="${CORC:-$root/compiler/bin/Release/net10.0/corc}"
corlink="${CORLINK:-$root/linker/bin/Release/net10.0/corlink}"
export CORC="$corc"
jobs="${CORSAC_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN)}"
mkdir -p "$root/build"
work="$(mktemp -d "$root/build/corsac-boot.XXXXXX")"
echo "CORSAC split-toolchain acceptance: $work"
trap 'echo "Build/boot failed; complete diagnostics: $work" >&2' ERR
revision="$(git -C "$os" rev-parse HEAD)"
mkdir -p "$work/source" "$work/root/boot" "$work/root/bin" "$work/root/etc" "$work/root/dev" "$work/root/proc" "$work/root/tmp" "$work/root/root"
# A fixed tracked-source snapshot does not race another kernel developer or
# alter their generated configuration, user binaries, or saved boot images.
git -C "$os" archive "$revision" | tar -x -C "$work/source"
snapshot="$work/source"
printf 'CORSAC commit: %s\nprofile: x86-486-isa\n' "$revision" > "$work/provenance.txt"
sha256sum "$corc" "$(dirname "$corc")/corc.dll" "$(dirname "$corlink")/corlink.dll" >> "$work/provenance.txt"
python3 "$snapshot/tools/kconfig.py" --root "$snapshot/os/kernel" x86-486-isa > "$work/config.log"
sources=()
while IFS= read -r line; do
    case "$line" in ''|'#'*) continue ;; esac
    sources+=("$snapshot/os/kernel/$line")
done < "$snapshot/os/kernel/config/sources.list"
"$corc" build --target x86-32 "$snapshot/os/kernel/arch/x86/entry.asm" --obj -o "$work/entry.o" > "$work/entry.log" 2>&1
"$corc" compile "${sources[@]}" --jobs "$jobs" --freestanding --obj \
    --asm-entry corc_start --tls-gs --cpu 486 --tag 'CORSAC/OS split-toolchain acceptance' \
    --stats -o "$work/kernel.o" > "$work/kernel.compile.log" 2>&1
"$corlink" "$work/entry.o" "$work/kernel.o" --entry _start --base 0xc0100000 --paddr 0x100000 \
    -o "$work/root/boot/kernel" > "$work/kernel.link.log" 2>&1
nm -n "$work/root/boot/kernel" > "$work/kernel.symbols"
readelf -lW "$work/root/boot/kernel" > "$work/kernel.segments"
"$corc" build --target x86-16 "$snapshot/os/boot/x86/stage1/stage1.asm" -o "$work/stage1.bin" > "$work/stage1.log" 2>&1
s2="$snapshot/os/boot/x86/stage2"
"$corc" compile "$s2/uart8250.cor" "$s2/vga.cor" "$s2/console.cor" "$s2/kbd8042.cor" "$s2/ide.cor" \
    "$snapshot/os/kernel/fs/block.cor" "$snapshot/os/kernel/fs/minixformat.cor" \
    "$snapshot/os/kernel/fs/minixread.cor" "$snapshot/os/kernel/fs/ext3.cor" "$s2/bootfs.cor" "$s2/stage2.cor" \
    --jobs "$jobs" --freestanding --flat --obj --cpu 486 --stats -o "$work/stage2.o" > "$work/stage2.compile.log" 2>&1
"$corlink" "$work/stage2.o" --flat --base 0x10000 -o "$work/stage2.bin" > "$work/stage2.link.log" 2>&1
# The boot gap on disk is larger than conventional RAM. Never attempt to
# execute an image that overlaps the VGA aperture/firmware region.
stage2_memory="$(sed -n 's/.* memory=\([0-9]*\).*/\1/p' "$work/stage2.link.log")"
test -n "$stage2_memory" && test "$stage2_memory" -le $((0x90000 - 0x10000))
for program in init mount agetty login sh reboot cat ps tty; do
    extra=()
    if [ "$program" = sh ]; then extra=("$snapshot/os/lib/shell.cor" "$snapshot/os/bin/edit.cor"); fi
    "$corc" compile "${extra[@]}" "$snapshot/os/bin/$program.cor" --jobs "$jobs" \
        -o "$work/root/bin/$program" > "$work/$program.compile.log" 2>&1
done
cp "$root/tests/integration/boot-root/etc/"* "$work/root/etc/"
cp "$root/tests/integration/boot-root/boot.lst" "$work/root/boot/boot.lst"
bash "$snapshot/tools/mkdisc.sh" "$work/disc.img" --size 32M --root "$work/root" \
    --boot-sector "$work/stage1.bin" --stage2 "$work/stage2.bin" > "$work/mkdisc.log" 2>&1
test "$(stat -c %s "$work/disc.img")" = 33554432
dotnet run --project "$root/tests/integration/boot/BootTests.csproj" -c Release -- "$work"
printf 'PASS actual stage2/kernel separate linking and 486 ISA boot: %s\n' "$work"
