#!/bin/sh
set -eu

EXPECTED_RGBDS_VERSION="rgbasm v1.0.1"
EXPECTED_CGB_SHA256="7b6b723d3d8f8df62e1987279da5825408d74dee8aea0fe0e0ef915a16ed7495"
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
RESOURCE_DIR="$SCRIPT_DIR/../Resources"

actual_version=$(rgbasm --version)
if [ "$actual_version" != "$EXPECTED_RGBDS_VERSION" ]; then
    echo "error: expected $EXPECTED_RGBDS_VERSION, found $actual_version" >&2
    exit 1
fi

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/gbzemu-bootroms.XXXXXX")
trap 'rm -rf "$work_dir"' EXIT HUP INT TERM

build_and_verify() {
    name="$1"
    expected_size="$2"
    source="$SCRIPT_DIR/$name.asm"
    embedded="$RESOURCE_DIR/$name.bin"
    object="$work_dir/$name.o"
    generated="$work_dir/$name.bin"

    if [ ! -f "$source" ]; then
        echo "error: firmware source missing: $source" >&2
        exit 1
    fi

    if [ ! -f "$embedded" ]; then
        echo "error: embedded firmware image missing: $embedded" >&2
        exit 1
    fi

    rgbasm -I "$SCRIPT_DIR/" -o "$object" "$source"
    rgblink -x -o "$generated" "$object"

    actual_size=$(wc -c < "$generated" | tr -d ' ')
    if [ "$actual_size" -ne "$expected_size" ]; then
        echo "error: generated $name.bin is $actual_size bytes, expected $expected_size" >&2
        exit 1
    fi

    if ! cmp -s "$generated" "$embedded"; then
        echo "error: generated $name.bin differs from $embedded" >&2
        exit 1
    fi

    digest=$(shasum -a 256 "$generated" | awk '{print $1}')
    if [ "$name" = "cgb_boot" ] && [ "$digest" != "$EXPECTED_CGB_SHA256" ]; then
        echo "error: generated cgb_boot.bin digest changed: $digest" >&2
        exit 1
    fi

    echo "verified $name.bin ($expected_size bytes, sha256 $digest)"
}

build_and_verify dmg_boot 256
build_and_verify mgb_boot 256
build_and_verify cgb_boot 2304
build_and_verify agb_boot 2304
build_and_verify sgb2_boot 256
