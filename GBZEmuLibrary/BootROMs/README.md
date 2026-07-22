# GBZEmu boot ROMs

This directory contains maintained source for the open startup firmware embedded by GBZEmuLibrary. Normal `dotnet build` uses the checked-in generated images under `GBZEmuLibrary/Resources/` and does not require RGBDS.

## Maintained images

| Hardware model | Source          | Embedded image  | Mapped size |
| -------------- | --------------- | --------------- | ----------: |
| DMG-B          | `dmg_boot.asm`  | `dmg_boot.bin`  |   256 bytes |
| MGB            | `mgb_boot.asm`  | `mgb_boot.bin`  |   256 bytes |
| CGB-E          | `cgb_boot.asm`  | `cgb_boot.bin`  | 2,304 bytes |
| AGB-A          | `agb_boot.asm`  | `agb_boot.bin`  | 2,304 bytes |
| SGB2           | `sgb2_boot.asm` | `sgb2_boot.bin` |   256 bytes |

Firmware availability and public model capability are validated separately. The AGB-A image and firmware slot are present for focused implementation testing, while `HardwareModelMetadata.ImplementedModels` continues to exclude AGB-A until its runtime, APU, state, and host paths pass the activation gates. Original SGB firmware is intentionally unsupported.

## Behavior

The firmware images are GBZEmu replacements, not Nintendo firmware dumps. They contain no Nintendo boot-ROM code or embedded Nintendo logo bytes. When a cartridge logo is displayed, it is read from that cartridge's header at runtime.

- **DMG-B:** displays the GBZEmu wordmark and cartridge-header logo, plays a synthesized two-note startup sequence, applies the DMG-B handoff state with `A=$01`, and transfers control to `$0100`.
- **MGB:** shares the project-authored monochrome presentation and deterministic handoff with DMG-B, except that it hands off with `A=$FF` as documented for MGB hardware.
- **CGB-E:** displays the GBZEmu wordmark, configures CGB palettes, preserves the documented CGB automatic compatibility-palette behavior for licensed DMG titles, applies the CGB-E handoff state, and transfers control to `$0100`.
- **AGB-A:** shares the project-authored color-family wordmark, diagonal reveal, cartridge-header mark, and two-note chime with CGB-E. It uses an AGB-inspired terminal blue and applies the documented later-AGB native-color or cartridge-dependent DMG-compatibility handoff before transferring control to `$0100`. This is compatibility firmware for `.gb`/`.gbc` cartridges, not native GBA firmware.
- **SGB2:** transfers the cartridge header through JOYP for the high-level-emulated SGB protocol, applies the SGB2 Game Boy-side handoff state, and transfers control to `$0100`. This is not a SNES-side SGB2 BIOS.

Built-in and external firmware use the same model-specific mapped-size contract. Skip boot does not map firmware and instead applies a deterministic model- and cartridge-specific handoff in the emulator core.

## Source and provenance

`dmg_boot.asm`, `mgb_boot.asm`, their shared `dmg_mgb_boot.inc`, `cgb_boot.asm`, `agb_boot.asm`, and the shared `cgb_agb_boot.inc` are GBZEmu project-authored replacement implementations. Their presentation and startup behavior were developed with public Game Boy documentation and emulator behavior as references. The color-family images include publicly documented factual compatibility data: title checksums/disambiguation, palette combinations, and RGB555 palette values described by [Pan Docs](https://gbdev.io/pandocs/Power_Up_Sequence.html#compatibility-palettes). The AGB handoff follows the publicly documented register and compatibility-mode behavior; the bounded terminal-blue substitution is authored presentation informed by SameBoy's Expat-licensed replacement firmware, not copied Nintendo data. This policy permits public factual compatibility tables and independently reimplemented behavior; it prohibits Nintendo code, graphics, logos, firmware bytes, assets, and sampled audio.

`sgb2_boot.asm` is derived from SameBoy's Expat-licensed `BootROMs/sgb_boot.asm`, adapted for GBZEmu's SGB2-only model and core handoff. The historical upstream SameBoy revision used for the first adaptation was not recorded and is therefore intentionally documented as unknown rather than guessed. The derivative was introduced to this repository in local commit `79faf8d`. SameBoy attribution and the full Expat notice are retained in `LICENSE` and `THIRD_PARTY_NOTICES.md`.

All source files in this directory and their generated images are distributed under the Expat License in `LICENSE`.

## Reproducible verification

Verification is pinned to **RGBDS 1.0.1**. Install that exact release, then run from the repository root:

```sh
./GBZEmuLibrary/BootROMs/verify.sh
```

The verifier:

1. rejects any other `rgbasm` version;
2. builds DMG-B, MGB, CGB-E, AGB-A, and SGB2 into a temporary directory;
3. validates exact mapped sizes;
4. pins the historical CGB-E image digest so source and embedded image cannot drift together;
5. byte-compares each generated image with `GBZEmuLibrary/Resources/*.bin`;
6. fails on a missing source/image or any generated difference;
7. prints the verified SHA-256 digest for each image.

The verifier never overwrites tracked embedded resources.
