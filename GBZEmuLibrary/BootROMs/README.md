# GBZEmu boot ROMs

This directory contains maintained source for the open startup firmware embedded by GBZEmuLibrary. Normal `dotnet build` uses the checked-in generated images under `GBZEmuLibrary/Resources/` and does not require RGBDS.

## Implemented images

| Hardware model | Source          | Embedded image  | Mapped size |
| -------------- | --------------- | --------------- | ----------: |
| DMG-B          | `dmg_boot.asm`  | `dmg_boot.bin`  |   256 bytes |
| CGB-E          | `cgb_boot.asm`  | `cgb_boot.bin`  | 2,304 bytes |
| SGB2           | `sgb2_boot.asm` | `sgb2_boot.bin` |   256 bytes |

MGB and AGB-A firmware are not present because those hardware models are not implemented. Original SGB firmware is intentionally unsupported.

## Behavior

The firmware images are GBZEmu replacements, not Nintendo firmware dumps. They contain no Nintendo boot-ROM code or embedded Nintendo logo bytes. When a cartridge logo is displayed, it is read from that cartridge's header at runtime.

- **DMG-B:** displays the GBZEmu wordmark and cartridge-header logo, plays a synthesized two-note startup sequence, applies the DMG-B handoff state, and transfers control to `$0100`.
- **CGB-E:** displays the GBZEmu wordmark, configures CGB palettes, preserves the documented CGB automatic compatibility-palette behavior for licensed DMG titles, applies the CGB-E handoff state, and transfers control to `$0100`.
- **SGB2:** transfers the cartridge header through JOYP for the high-level-emulated SGB protocol, applies the SGB2 Game Boy-side handoff state, and transfers control to `$0100`. This is not a SNES-side SGB2 BIOS.

Built-in and external firmware use the same model-specific mapped-size contract. Skip boot does not map firmware and instead applies a deterministic model- and cartridge-specific handoff in the emulator core.

## Source and provenance

`dmg_boot.asm` and `cgb_boot.asm` are GBZEmu project-authored replacement implementations. Their presentation and startup behavior were developed with public Game Boy documentation and emulator behavior as references. The CGB image includes publicly documented factual compatibility data: title checksums/disambiguation, palette combinations, and RGB555 palette values described by [Pan Docs](https://gbdev.io/pandocs/Power_Up_Sequence.html#compatibility-palettes). This policy permits factual compatibility data and independently reimplemented behavior; it does not permit copying Nintendo firmware code, graphics, firmware bytes, or sampled audio.

`sgb2_boot.asm` is derived from SameBoy's Expat-licensed `BootROMs/sgb_boot.asm`, adapted for GBZEmu's SGB2-only model and core handoff. The historical upstream SameBoy revision used for the first adaptation was not recorded and is therefore intentionally documented as unknown rather than guessed. The derivative was introduced to this repository in local commit `79faf8d`. SameBoy attribution and the full Expat notice are retained in `LICENSE` and `THIRD_PARTY_NOTICES.md`.

All source files in this directory and their generated images are distributed under the Expat License in `LICENSE`.

## Reproducible verification

Verification is pinned to **RGBDS 1.0.1**. Install that exact release, then run from the repository root:

```sh
./GBZEmuLibrary/BootROMs/verify.sh
```

The verifier:

1. rejects any other `rgbasm` version;
2. builds DMG-B, CGB-E, and SGB2 into a temporary directory;
3. validates exact mapped sizes;
4. byte-compares each generated image with `GBZEmuLibrary/Resources/*.bin`;
5. fails on a missing source/image or any generated difference;
6. prints the verified SHA-256 digest for each image.

The verifier never overwrites tracked embedded resources.
