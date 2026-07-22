# Canonical conformance applicability

This document records the approved Phase 4 applicability policy for the committed conformance fixtures. Fixture origin and licensing remain documented in [README.md](README.md).

## Policy dimensions

Keep these dimensions independent:

1. **Physical fixture** — one committed ROM and its stable fixture ID.
2. **Concrete execution** — a reviewed fixture/model row on one canonical target: DMG-B, MGB, CGB-E, SGB2, or AGB-A. A fixture may produce zero, one, or several executions. Its primary execution keeps the bare fixture ID; additional models use `@DmgB`, `@Mgb`, `@CgbE`, `@Sgb2`, or `@AgbA`.
3. **Startup circumstance** — `SyntheticSkipBoot` validates the harness's synthetic post-boot state; it is not an official-firmware run. `MatchingFirmwareRequired` means the expected phase or state cannot be reproduced by synthetic skip boot and remains a specifically explained visible skip until a matching firmware lane exists.
4. **Oracle provenance** — an embedded result protocol can be revision-independent, while a framebuffer oracle belongs to the hardware revision that produced it. The execution model never makes a cross-revision image valid by implication.

Applicability, executability, and oracle validity are therefore separate decisions. Existing executions stay active when applicability is merely unknown. A new model pair or oracle decision marked unresolved produces neither an execution nor an xUnit skip; it remains in the locked unresolved ledger until evidence resolves it. Visible skips require evidence for an exact unsupported revision/model or unavailable startup circumstance. Current emulator output must never determine applicability.

## Pinned evidence

| Suite     | Pinned source                                                                                 |
| --------- | --------------------------------------------------------------------------------------------- |
| Mooneye   | `31510e12eea6286d36eea060a6adde755e1067aa`                                                    |
| SameSuite | `f15645fb049a47ea235f6d2c9a033e72d8087901`                                                    |
| Mealybug  | `70e88fb90b59d19dfbb9c3ac36c64105202bb1f4`; mgblib `1d9045a4b4cbd1ec5223e672a1cef965e9fcd194` |

Pinned Mooneye tags mean `G = DMG + MGB`, `S = SGB + SGB2`, `C = CGB + AGB + AGS`, and `A = AGB + AGS`; combinations are unions. Exact suffixes such as `dmg0`, `dmgABCmgb`, `mgb`, `sgb`, and `sgb2` override those groups. SameSuite compact suffixes name exact sets: for example, `cgb0BC` means CPU CGB-0/B/C and does not imply CGB-A.

## Evidence keys

`RomApplicability.cs` uses these keys verbatim:

| Evidence key                                 | Decision                                                                                                                          |
| -------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `samesuite-agb-applicability-unresolved`     | Pinned SameSuite evidence does not establish AGB-A applicability for an untagged CGB APU, DMA, or PPU fixture.                    |
| `samesuite-sgb2-expected-arrays-unresolved`  | The two committed SameSuite SGB expected arrays target original SGB mode and are not established as SGB2 evidence.                |
| `mealybug-agb-ppu-applicability-unresolved`  | Pinned Mealybug evidence does not establish the PPU fixture on AGB-A.                                                             |
| `mealybug-agb-hdma-applicability-unresolved` | No reviewed pinned-source evidence establishes the HDMA result fixture on AGB-A.                                                  |
| `mealybug-scy-cgb-d-and-later`               | The CPU CGB-C SCY images for `m3_scy_change` and `m3_scy_change2` are known inapplicable on CPU CGB-D and later, including CGB-E. |
| `mealybug-no-cgb-e-framebuffer-oracle`       | No committed CPU CGB-E image or reviewed exact-equivalence evidence exists for the other CPU CGB-C framebuffer oracles.           |

## Canonical matrices

`execute` means a concrete xUnit row. `skip` means a reviewed visible skip. `unresolved` remains only in the policy ledger. `—` means no Phase 4 row.

### Mooneye

| Fixture group                                                                   |                                DMG-B |                                  MGB |   CGB-E |                             SGB2 |   AGB-A |
| ------------------------------------------------------------------------------- | -----------------------------------: | -----------------------------------: | ------: | -------------------------------: | ------: |
| Exact `dmg0` boot fixtures                                                      |                                 skip |                                    — |       — |                                — |       — |
| `boot_regs-dmgABC`                                                              |                              execute |                                    — |       — |                                — |       — |
| `boot_div-dmgABCmgb`, `boot_hwio-dmgABCmgb`, `serial/boot_sclk_align-dmgABCmgb` | execute/skip by startup circumstance | execute/skip by startup circumstance |       — |                                — |       — |
| `boot_regs-mgb`                                                                 |                                    — |                              execute |       — |                                — |       — |
| `boot_regs-sgb2`                                                                |                                    — |                                    — |       — |                          execute |       — |
| `boot_div-S`, `boot_div2-S`, `boot_hwio-S`                                      |                                    — |                                    — |       — | skip: matching firmware required |       — |
| Nine `-GS` acceptance fixtures                                                  |                              execute |                              execute |       — |                          execute |       — |
| Remaining untagged acceptance fixtures                                          |                              execute |                              execute | execute |                          execute | execute |
| Emulator-only MBC1/MBC2/MBC5                                                    |               one baseline execution |                                    — |       — |                                — |       — |

The mapper fixtures validate a model-neutral cartridge contract and remain single-run. The untagged acceptance fan-out is a reviewed acceptance-lane decision, not a claim that upstream supplied a per-model oracle.

Exact original-SGB evidence is not relabeled as SGB2. These committed fixtures are excluded from active executions:

- `mooneye/acceptance/boot_regs-sgb`
- `samesuite/sgb/command_mlt_req`
- `samesuite/sgb/command_mlt_req_1_incrementing`

### SameSuite

| Fixture group                                      |   DMG-B |     MGB |                      CGB-E |       SGB2 |      AGB-A |
| -------------------------------------------------- | ------: | ------: | -------------------------: | ---------: | ---------: |
| APU `div_write_trigger`, `div_write_trigger_10`    | execute | execute |                    execute |    execute | unresolved |
| Other untagged APU ROMs                            |       — |       — |                    execute |          — | unresolved |
| APU suffixes `-cgb0B`, `-cgb0BC`, `-cgb0`, `-cgbB` |       — |       — | skip: exact non-E revision |          — |          — |
| `channel_1_freq_change_timing-cgbDE`               |       — |       — |                    execute |          — |          — |
| `channel_1_freq_change_timing-A`                   |       — |       — |                          — |          — |    execute |
| Four DMA ROMs                                      |       — |       — |                    execute |          — | unresolved |
| `ppu/blocking_bgpi_increase`                       |       — |       — |                    execute |          — | unresolved |
| `interrupt/ei_delay_halt`                          | execute | execute |                    execute |    execute |    execute |
| Two original-SGB ROMs                              |       — |       — |                          — | unresolved |          — |

### Mealybug

| Fixture group                                    |                                               CGB-E |      AGB-A |
| ------------------------------------------------ | --------------------------------------------------: | ---------: |
| `m3_scy_change`, `m3_scy_change2` CPU CGB-C PNGs | no framebuffer execution; known-inapplicable oracle | unresolved |
| Other 29 CPU CGB-C PNGs                          |         no framebuffer execution; unresolved oracle | unresolved |
| `win_without_bg`                                 |               execute with embedded result protocol | unresolved |
| `hdma_during_halt-C`, `hdma_timing-C`            |               execute with embedded result protocol | unresolved |
| `mbc3_rtc`                                       |                  one revision-independent execution | no fan-out |

All 31 committed PNGs remain unchanged CPU CGB-C evidence. They must not gate CGB-E executions unless a separately reviewed compatibility key establishes exact cross-revision validity. Removing those invalid image assertions is an oracle-policy correction, not an emulator fix. `win_without_bg`, HDMA, and MBC3 RTC continue to use their embedded result protocols.

## Suites not expanded in Phase 4

Blargg, dmg-acid2, and cgb-acid2 retain their existing suite-selected baseline models and failures. Phase 4 does not fan them out across canonical models because the pinned evidence audit did not establish per-model matrices for those legacy artifacts. Any later expansion requires a separate evidence-backed review.
