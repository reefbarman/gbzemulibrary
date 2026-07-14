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
- `GBZEmuLibrary/Core/BootROM.cs`: intentionally empty boot-ROM distribution shim.
- `GBZEmuLibrary/GBZEmuLibrary.csproj`: classic explicit-file-list .NET Framework project.
- `README.md`: user-facing behavior, integration contract, and known limitations.

## Toolchain and compatibility constraints

- The project targets **.NET Framework 3.5** and has no package dependencies.
- Preserve .NET Framework 3.5 BCL compatibility. Do not introduce APIs from newer framework targets without an intentional project migration.
- This is a classic, non-SDK-style `.csproj`. Every new `.cs` file must be added to an explicit `<Compile Include="..." />` entry or it will not be built.
- Keep the assembly Any CPU and engine-neutral unless a task explicitly changes those constraints.
- The existing source uses some C# 6-era syntax despite targeting the .NET 3.5 API surface. Use a compiler compatible with the existing code; do not assume the oldest Unity C# compiler can compile source files directly.
- Do not add Unity assemblies or types to the core. A Unity integration should reference the built DLL or live in a separate adapter project.

## Architectural invariants

### Timing

The CPU is the timing source. CPU operations call `IncrementClock`, which reaches `Emulator.UpdateSystems()` through `CPU.OnClockTick`. The divider, timer, GPU, and APU must advance from those same cycle counts.

When changing instruction or interrupt behavior:

- preserve clock increments on both taken and untaken branches;
- account for CGB double-speed conversion in `Emulator.UpdateSystems()`;
- check timer, PPU, APU, and interrupt side effects, not only register results;
- do not add independent wall-clock timing inside a subsystem.

`Emulator.Update()` runs until one nominal 60 Hz cycle budget is consumed. Real-time pacing belongs to the host.

### Memory routing

`MMU` builds a complete address-to-`IMemoryUnit` lookup in its constructor. Device ordering in the `memoryUnits` list determines which unit owns overlapping addresses; changing that order can silently reroute memory.

- Keep `CanReadWriteByte`, `ReadByte`, and `WriteByte` ranges mutually consistent.
- Preserve Game Boy address semantics, including mirrored work RAM, unusable memory, OAM, I/O registers, and boot-ROM overlay behavior.
- `MainMemory` is the fallback store and is not included in the ownership-probing list; its throwing `CanReadWriteByte()` is therefore not currently called during MMU setup.
- DMA uses `MessageBus` memory callbacks. Avoid bypassing MMU side effects when copying data.

### Shared message bus and instance lifetime

`MessageBus` is a static singleton. Constructing another emulator overwrites some callbacks and adds more event subscriptions. The current architecture supports one live `Emulator` per process.

Do not claim multi-instance or thread-safe support without first removing this global coupling and adding coverage for lifecycle cleanup. If adding subscriptions, ensure they cannot leak across emulator lifetimes.

### Public host contract

Treat these as compatibility-sensitive:

- `Emulator.Start`, `Update`, `Terminate`, input methods, channel toggles, and output getters;
- `Emulator.Config` fields and `BootMode` values;
- `Display` dimensions and `Sound.SAMPLE_RATE`;
- framebuffer indexing as `Color[x, y]` at 160×144;
- audio as fixed-rate interleaved `left, right` byte amplitudes;
- `JoypadButtons` numeric ordering, which maps directly onto joypad-register bits;
- save naming as `<full ROM filename>.sav`.

`GetScreenData()` and `GetSoundSamples()` expose reused internal buffers. The audio array is cleared and reused by the next `GetSoundSamples()` call, so asynchronous hosts must copy it first. Avoid hidden allocations or format changes in these hot paths unless the API change is deliberate and documented.

`Terminate()` flushes/closes cartridge RAM and is not safe to call before a successful `Start()` under the current implementation. Preserve save data on all normal shutdown paths.

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
- Known gaps include MBC2 internal nibble RAM, MBC3 RTC/latching/persistence, nonfunctional MBC3/MBC5 external-RAM bank selection, and ROMs larger than the 2 MiB `_cartMemory` buffer.
- Save directories are not created automatically. Saves are file-backed and named `<ROM filename>.sav`.
- Changes to bank masking, RAM enable, or save sizing must be checked against each affected MBC family.
- Never commit commercial ROMs, proprietary boot ROMs, or user `.sav` files. Use redistributable emulator test ROMs only when their license permits it.

### Boot behavior

`Core/BootROM.cs` contains empty arrays as a distribution shim. Normal boot-ROM execution is therefore not functional in this checkout, and the current DMG skip path also indexes the empty short-DMG array. `BootMode.Short` is declared but is not read by `Emulator.Start()`.

Do not fill the arrays with copyrighted firmware. Prefer a legal runtime-supplied boot-ROM interface or a correct post-boot initialization path. Any boot change must cover DMG-only, CGB-compatible, and CGB-only cartridge headers plus `DMG`, `GBC`, `Skip`, and `Force` combinations.

## Coding conventions

- Match the existing namespace (`GBZEmuLibrary`), brace style, indentation, and partial-class organization.
- Keep changes targeted. Emulator correctness is sensitive to unrelated timing and memory refactors.
- Prefer named hardware constants over new magic addresses or bit positions.
- Keep public API additions minimal and engine-agnostic.
- Do not add comments that merely restate code; document hardware quirks, timing rationale, or non-obvious compatibility decisions.
- Avoid allocations, LINQ, reflection, logging, and exceptions in instruction, pixel, and sample hot paths. Existing uses are not a reason to add more.
- When fixing a hardware quirk, cite a stable public specification or test ROM in the PR/commit context when possible.

## Build and validation

Canonical release build on a machine with MSBuild and the .NET Framework 3.5 targeting pack:

```powershell
msbuild GBZEmuLibrary.sln /p:Configuration=Release
```

A compatible Mono installation may use:

```sh
xbuild GBZEmuLibrary.sln /p:Configuration=Release
```

Expected artifact:

```text
GBZEmuLibrary/bin/Release/GBZEmuLibrary.dll
```

`dotnet build` is not the canonical validation command for this legacy project format/target.

There is currently no automated test project. For behavior changes:

1. Build both Debug and Release when the toolchain is available.
2. Add focused automated tests with legally redistributable fixtures when practical.
3. Otherwise document the exact test ROM/game, boot mode, affected hardware mode, and observed result.
4. Exercise at least one DMG path and one CGB path when shared CPU/MMU/PPU behavior changes.
5. Check save creation/reload for cartridge changes and audio/video buffer contracts for facade changes.
6. Keep smoke-test notes concise and checklist-based.

Recommended emulator validation areas include public-domain test ROM suites for CPU instructions, instruction timing, interrupts, memory behavior, PPU timing/rendering, and APU register/output behavior. Record suite names and individual pass/fail cases; do not summarize an unverified run as full compatibility.

## Documentation requirements

Update `README.md` whenever a change affects:

- build requirements or target framework;
- public lifecycle, input, video, or audio contracts;
- boot-ROM expectations or `BootMode` behavior;
- supported cartridge controllers or known hardware gaps;
- save filename/location behavior;
- Unity/host integration constraints.

Keep capability statements evidence-based. Distinguish header recognition or implemented code paths from verified game compatibility.

## Before submitting a change

- Confirm every new source file is included in `GBZEmuLibrary.csproj`.
- Review cycle counts and memory ownership for affected paths.
- Check for accidental host-framework dependencies.
- Check for copyrighted ROM/firmware/save artifacts.
- Build with the legacy target when tools are available.
- Run or document focused emulator validation.
- Update README limitations rather than hiding known incompatibilities.
