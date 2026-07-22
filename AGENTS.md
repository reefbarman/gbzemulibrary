# AGENTS.md

## Purpose and scope

GBZEmuLibrary is an embeddable Game Boy/Game Boy Color emulator core. It is a class library, not a standalone emulator frontend. Keep the core independent of Unity, rendering APIs, audio devices, UI frameworks, and platform-specific input systems; those belong in host adapters outside this project.

These instructions apply to the entire repository.

## Repository map

- `GBZEmuLibrary/Emulator.cs`: public lifecycle and host-facing API.
- `GBZEmuLibrary/Core/CPU/`: LR35902 execution, instruction maps, flags, interrupts, and timing.
- `GBZEmuLibrary/Core/GPU/`: DMG/CGB scanline rendering, VRAM/OAM/registers, and RGB framebuffer.
- `GBZEmuLibrary/Core/APU/`: four sound channels, frame sequencer, mixing, and sample buffering.
- `GBZEmuLibrary/Core/Cartridge/`: cartridge headers, MBC banking, and file-backed RAM.
- `GBZEmuLibrary/Core/Memory/`: MMU routing, main/work RAM, and DMA.
- `GBZEmuLibrary/Core/Schemas.cs`: memory map, hardware constants, and public display/audio/input types.
- `GBZEmuLibrary/Core/MessageBus.cs`: process-global internal callbacks for interrupts, memory access, and HBlank.
- `GBZEmuLibrary/Core/BootROM.cs`: runtime storage and validation for host-supplied boot-ROM bytes.
- `GBZEmuLibrary/GBZEmuLibrary.csproj`: SDK-style `netstandard2.0` library project.
- `GBZEmuFrontend/`: minimal cross-platform Raylib-cs test host for video, audio, input, ROMs, and boot ROMs.
- `GBZEmuHeadless/`: deterministic command-line ROM runner for frame captures, input sequences, boot-mode checks, and JSON state/hash reports.
- `GBZEmuTests/`: serialized xUnit harness, debug-tooling tests, test-ROM fixtures, and framebuffer references.
- `README.md`: user-facing behavior, integration contract, evidence-based test results, and known limitations.

## Toolchain and compatibility constraints

- The core targets **`netstandard2.0`** and has no package dependencies. Preserve compatibility with current Unity versions that consume `netstandard2.0` managed plug-ins.
- The core project is SDK-style and uses implicit source globs; new `.cs` files under `GBZEmuLibrary/` are included automatically.
- Keep the library assembly Any CPU and engine-neutral unless a task explicitly changes those constraints.
- The frontend targets `net10.0` and uses Raylib-cs. Keep Raylib, rendering, audio-device, window, and platform input dependencies out of the core project.
- Do not add Unity assemblies or types to the core. A Unity integration should reference `GBZEmuLibrary/bin/<Configuration>/netstandard2.0/GBZEmuLibrary.dll` or live in a separate adapter project.

## Architectural invariants

### Timing

The CPU is the timing source. CPU operations call `IncrementClock`, which reaches `Emulator.UpdateSystems()` through `CPU.OnClockTick`. The divider, timer, GPU, and APU must advance from those same cycle counts.

When changing instruction or interrupt behavior:

- preserve clock increments on both taken and untaken branches;
- account for CGB double-speed conversion in `Emulator.UpdateSystems()`;
- check timer, PPU, APU, and interrupt side effects, not only register results;
- do not add independent wall-clock timing inside a subsystem.

`Emulator.Update()` runs until one 70,224-cycle hardware frame budget is consumed (approximately 59.7275 Hz). Real-time pacing belongs to the host.

### Memory routing

`MMU` builds a complete address-to-`IMemoryUnit` lookup in its constructor. Device ordering in the `memoryUnits` list determines which unit owns overlapping addresses; changing that order can silently reroute memory.

- Keep `CanReadWriteByte`, `ReadByte`, and `WriteByte` ranges mutually consistent.
- Preserve Game Boy address semantics, including mirrored work RAM, unusable memory, OAM, I/O registers, and boot-ROM overlay behavior.
- `MainMemory` is the fallback store and is not included in the ownership-probing list; its throwing `CanReadWriteByte()` is therefore not currently called during MMU setup.
- DMA uses `MessageBus` memory callbacks. Avoid bypassing MMU side effects when copying data.

### Shared message bus and instance lifetime

`MessageBus` is a static singleton. Constructing another emulator replaces the interrupt, HBlank, and memory callback owners; sequential instances are covered by tests, but the current architecture still supports only one live `Emulator` per process.

Do not claim concurrent multi-instance or thread-safe support without first removing this global coupling and adding coverage for lifecycle cleanup. If adding subscriptions, preserve single-owner replacement semantics so callbacks cannot leak across emulator lifetimes.

### Public host contract

Treat these as compatibility-sensitive:

- `Emulator.Start`, `Update`, `Terminate`, input methods, channel toggles, and output getters;
- `Emulator.Config(HardwareModel)`, `HardwareModel` values, and immutable `BootRomConfig` choices;
- `Display` dimensions and `Sound.SAMPLE_RATE`;
- framebuffer indexing as `Color[x, y]` at 160×144;
- audio as fixed-rate interleaved `left, right` byte amplitudes;
- `JoypadButtons` numeric ordering, which maps directly onto joypad-register bits;
- save naming as `<full ROM filename>.sav`.

`GetScreenData()` and `GetSoundSamples(out int sampleFrameCount)` expose reused internal buffers. Only the first `sampleFrameCount * 2` audio bytes are valid. The audio array is cleared and reused by the next `GetSoundSamples(...)` call, so asynchronous hosts must copy it first. Avoid hidden allocations or format changes in these hot paths unless the API change is deliberate and documented.

`Terminate()` flushes/closes cartridge RAM and is idempotent. An `Emulator` instance permits one successful `Start()`; hosts must construct a new instance to load or restart a ROM. Preserve save data on all normal and failed-start shutdown paths.

## Hardware-area guidance

### CPU

- Base and CB-prefixed opcode maps are declared separately.
- Invalid/unmapped opcodes currently throw `NotImplementedException`; do not silently treat them as NOPs without evidence.
- Keep flag behavior and cycle timing together when implementing an instruction.
- Pay special attention to delayed `EI`/`DI`, HALT behavior, interrupt priority, stack byte order, DAA, and signed SP-relative operations.
- STOP handling is currently incomplete; do not describe it as complete without implementing and validating it.

### GPU

- Rendering is scanline-based and writes `_screenData[x, ScanLine]`.
- Preserve DMG and CGB differences in palettes, tile attributes, sprite priority, VRAM banks, work-RAM banks, and DMA.
- Coordinate or buffer-layout changes affect every host renderer and require README updates.
- PPU timing changes should be tested around mode transitions, LY/LYC coincidence, VBlank, HBlank DMA, window positioning, and the 10-sprite-per-line rule.

### APU

- Output is fixed at 44,100 Hz and double-buffered.
- Buffer bytes are interleaved left/right channel amplitudes, not a host audio API abstraction.
- Preserve frame-sequencer rates, length/envelope/sweep behavior, DAC enable semantics, and NR52 power behavior.
- Avoid adding logging in per-sample or per-cycle paths.

### Cartridges and saves

- Header parsing recognizes ROM-only, MBC1, MBC2, MBC3, and MBC5 families, but support is partial.
- MBC1 uses independent BANK1/BANK2/mode mapping with MBC1M detection; MBC2 includes persistent 512×4-bit RAM; MBC5 supports 9-bit ROM banking and dynamically sized ROM storage. The committed Mooneye MBC1/MBC2/MBC5 geometry cases pass.
- Known gaps include MBC3 RTC/latching/persistence and MBC3 external-RAM bank selection; broader game compatibility remains unverified.
- Save directories are not created automatically. Saves are file-backed and named `<ROM filename>.sav`.
- Changes to bank masking, RAM enable, or save sizing must be checked against each affected MBC family.
- Never commit commercial ROMs, proprietary boot ROMs, or user `.sav` files. Use redistributable emulator test ROMs only when their license permits it.

### Boot behavior

`Emulator.Config(HardwareModel)` requires a concrete model and accepts one immutable `BootRomConfig`: built-in firmware, an external file, external bytes, or skip. `Core/BootROM.cs` validates exact model-specific images: 256 bytes for DMG-B, MGB, and SGB2, and 2304 bytes for CGB-E. External byte-backed configuration and active firmware storage must remain privately owned. Built-in replacement firmware is maintained under `GBZEmuLibrary/BootROMs/`; official Nintendo firmware is never distributed.

Any boot change must cover DMG-only, CGB-compatible, and CGB-only cartridge headers across implemented DMG-B, MGB, CGB-E, and SGB2 models and all four firmware choices where applicable. AGB-A must fail as a defined but unimplemented model before firmware lookup. Preserve the DMG-B/MGB/SGB2 overlay at `0x0000–0x00FF`, the additional CGB-E overlay at `0x0200–0x08FF`, and fallback to cartridge ROM outside the selected image.

## Coding conventions

- Match the existing namespace (`GBZEmuLibrary`), brace style, indentation, and partial-class organization.
- Keep changes targeted. Emulator correctness is sensitive to unrelated timing and memory refactors.
- Prefer named hardware constants over new magic addresses or bit positions.
- Keep public API additions minimal and engine-agnostic.
- Add XML documentation comments to classes and methods that explain their purpose, emulated hardware role, inputs, outputs, or important side effects. When changing existing code, add missing documentation to the classes and methods you touch.
- Use inline comments for hardware quirks, timing rationale, or non-obvious compatibility decisions; do not add comments that merely restate code.
- Avoid allocations, LINQ, reflection, logging, and exceptions in instruction, pixel, and sample hot paths. Existing uses are not a reason to add more.
- When fixing a hardware quirk, cite a stable public specification or test ROM in the PR/commit context when possible.

## Build and validation

Canonical release build with the current LTS .NET SDK:

```sh
dotnet build GBZEmuLibrary.sln -c Release
```

Expected library artifact:

```text
GBZEmuLibrary/bin/Release/netstandard2.0/GBZEmuLibrary.dll
```

The frontend artifact is under `GBZEmuFrontend/bin/Release/net10.0/`. Raylib-cs supplies native binaries for supported macOS, Windows, and Linux runtime identifiers.

Run the serialized xUnit suite for behavior changes:

```sh
dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release
```

The harness discovers all fixtures under `GBZEmuTests/Fixtures/` and reports each ROM's actual oracle result. Passing ROMs pass and failing ROMs fail; the complete suite is expected to remain red until its conformance gaps are fixed. Treat `ExpectedRomIds.txt` and current test output as the authoritative sources for fixture inventory and results.

Use `GBZEmuHeadless` for deterministic visual and runtime investigation when a ROM needs frame-by-frame evidence, scripted input, boot handoff comparison, or scene-transition capture:

```sh
dotnet run --project GBZEmuHeadless -c Release -- <rom-path> \
  --frames 900 \
  --capture-frames 650-900 \
  --capture-every 10 \
  --output runtime/captures/<case>
```

Frames are numbered from 1 after each `Emulator.Update()`. Use repeated `--input <frame>:<button>:down|up` options for deterministic input, `--model <DmgB|CgbE|Sgb2|Mgb|AgbA>` for concrete hardware selection, and either `--bootrom <path>` or `--skip-bootrom` for firmware policy. Inspect the generated PPM frames and compare `report.json` framebuffer, palette, VRAM, CPU, and PPU hashes/state. Give each run an isolated `--output` directory and do not commit captures, commercial ROMs, proprietary firmware, or generated saves. Headless evidence supplements focused xUnit tests and committed ROM oracles; it does not replace or weaken them.

For behavior changes:

1. Build both Debug and Release when the toolchain is available.
2. Run focused tests while iterating, then the full Release suite.
3. Add focused automated tests or legally redistributable fixtures for new behavior.
4. Let newly passing ROMs turn green without requiring a baseline update.
5. Skip a ROM only when it requires a deliberately unsupported hardware mode or test circumstance, and include a specific visible skip reason. Do not skip ordinary emulator correctness failures.
6. Exercise at least one DMG path and one CGB path when shared CPU/MMU/PPU behavior changes.
7. Check save creation/reload for cartridge changes and audio/video buffer contracts for facade changes.
8. Use deterministic headless captures for visual regressions, boot transitions, or input-driven runtime behavior that focused register tests cannot fully observe.
9. Keep smoke-test notes concise and checklist-based.

Fixture provenance and licensing are documented in `GBZEmuTests/Fixtures/README.md`. Blargg has no explicit upstream license; this repository commits those binaries with attribution by an explicit owner decision. Never generalize that exception to commercial ROMs, firmware, saves, or other unlicensed fixtures.

## Documentation requirements

Update `README.md` whenever a change affects:

- build requirements or target framework;
- public lifecycle, input, video, or audio contracts;
- hardware-model compatibility or boot-ROM source behavior;
- supported cartridge controllers or known hardware gaps;
- save filename/location behavior;
- Unity/host integration constraints.

Keep capability statements evidence-based. Distinguish header recognition or implemented code paths from verified game compatibility.

## Before submitting a change

- Confirm new core source files remain under the SDK-style `GBZEmuLibrary/` project root.
- Review cycle counts and memory ownership for affected paths.
- Check for accidental host-framework dependencies.
- Check for copyrighted ROM/firmware/save artifacts.
- Build the full solution in Debug and Release with `dotnet build`.
- Run or document focused emulator validation.
- Update README limitations rather than hiding known incompatibilities.
