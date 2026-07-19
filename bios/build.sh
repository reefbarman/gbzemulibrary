#!/bin/sh
# Builds the GBZEmu custom boot ROMs into the library's embedded resources.
# Requires RGBDS (https://rgbds.gbdev.io): brew install rgbds
set -eu

cd "$(dirname "$0")"

OUT_DIR="../GBZEmuLibrary/Resources"
mkdir -p "$OUT_DIR"

build() {
    name="$1"
    size="$2"
    rgbasm -o "$name.o" "$name.asm"
    rgblink -x -o "$OUT_DIR/$name.bin" "$name.o"
    rm -f "$name.o"
    actual=$(wc -c < "$OUT_DIR/$name.bin" | tr -d ' ')
    if [ "$actual" -ne "$size" ]; then
        echo "error: $name.bin is $actual bytes, expected $size" >&2
        exit 1
    fi
    echo "built $name.bin ($size bytes)"
}

build dmg_boot 256
build cgb_boot 2304
build sgb_boot 256
build sgb2_boot 256
