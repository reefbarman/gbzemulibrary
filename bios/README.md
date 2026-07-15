# GBZEmu boot ROMs

Original replacement boot ROMs in the spirit of the real firmware (and of
SameBoy's replacements): a large pixel-doubled **GBZEmu** wordmark is the
hero graphic, with the **cartridge header's own logo** and a
registered-trademark symbol rendered beneath it exactly like real firmware
draws them. The classic two-note chime plays before hand-off. The images
contain no Nintendo code or logo data — the logo graphic is read from the
cartridge at `$0104-$0133` at boot, just like real hardware — and they
perform no logo/checksum lock-up, so any cartridge boots. The wordmark is
stored in the same nibble format as the header logo, so one decoder
renders both.

| Image | Size | Boot behavior |
| --- | --- | --- |
| `dmg_boot.asm` | 256 bytes | The lockup scrolls down the screen, chime, hand off (~3.1 s; ~1.2 s with `BootMode.Short`) |
| `cgb_boot.asm` | 2304 bytes | Static lockup on white; a rainbow color sweep washes through the wordmark, chime, hand off (~2 s) |

The built binaries are embedded into `GBZEmuLibrary` as
`GBZEmuLibrary/Resources/*.bin` and are used automatically for any slot
without a host-supplied image (unless `BootMode.Skip` is set).

## Behavior notes

- Both images hand off with the same CPU register and I/O state as the
  emulator's skip-boot profile, so games cannot tell the difference
  (`GBZEmuTests/BootRomTests.cs` enforces this).
- To fit 256 bytes, `dmg_boot` relies on two GBZEmu reset guarantees:
  VRAM starts zeroed and the APU reset profile is already applied. It is
  emulator-specific and would need those steps added for real hardware.
- `dmg_boot` reads its scroll speed from byte `$00FD`; `BootMode.Short`
  patches that byte to 3, which also divides the logo hold.
- `cgb_boot` installs a white-to-black grayscale ramp as BG/OBJ palette 0 —
  the palette DMG carts keep in compatibility mode, matching real-hardware
  defaults for unlisted carts. The color sweep runs on BG palettes 1-6 via
  the bank-1 attribute map (palette 7 renders the header logo), and the
  attributes are cleared again before hand-off.
- `cgb_boot` also runs cleanly when a DMG-only cart boots in DMG mode: it
  detects that mode via a KEY1 read and skips VBK/attribute work, and the
  ignored palette writes leave a plain dark-on-light logo.
- `cgb_boot` carries the DMG title-checksum table at the stock offsets
  (`$06C7-$0716`) that `CartridgeHeader` reads to grant known DMG carts a
  compatibility palette. The checksums are factual data (sums of cartridge
  title bytes, documented in Pan Docs).

## Building

Requires [RGBDS](https://rgbds.gbdev.io) (`brew install rgbds`):

```sh
./build.sh
```

The script assembles both images, verifies their sizes, and writes them to
`GBZEmuLibrary/Resources/`.
