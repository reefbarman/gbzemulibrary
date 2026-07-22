# GBZEmuLibrary

GBZEmuLibrary is an embeddable Game Boy and Game Boy Color emulator core written in C#. It provides CPU, graphics, audio, cartridge, memory, timer, DMA, and joypad emulation without owning a window, renderer, audio device, or input system.

The library is intended to sit behind a host such as Unity or another C# engine: the host advances emulation, uploads the framebuffer to its own texture, submits audio to its own mixer, and forwards input events.

> **Project status:** experimental and compatibility-driven. The repository includes an automated ROM-conformance suite whose individual failures are reported directly by the test runner. This is not a game compatibility matrix. See [Automated testing](#automated-testing) and [Current limitations](#current-limitations) before integrating it.

## Highlights

- Engine-neutral `Emulator` facade with no Unity or other third-party dependencies.
- LR35902 CPU instruction dispatch, including the CB-prefixed instruction set, interrupts, HALT handling, and CGB double-speed state.
- Scanline-based DMG/CGB graphics with backgrounds, windows, sprites, palettes, VRAM banking, and DMA paths.
- Four Game Boy audio channels: two square channels, the programmable wave channel, and the noise channel.
- ROM-only, MBC1, MBC2, MBC3, and MBC5 cartridge header/banking paths, including MBC5 rumble output.
- File-backed external cartridge RAM.
- Engine-neutral Game Genie and GameShark/Action Replay parsing, lifecycle, and deterministic application.
- Public RGB framebuffer, stereo sample buffer, and joypad API designed for host-engine adapters.
- Targets `netstandard2.0` for compatibility with current Unity versions and modern .NET hosts.
- Includes a small cross-platform Raylib-cs frontend for interactive video, audio, and input testing.

## Host API

Most integrations only need `GBZEmuLibrary.Emulator`:

| API                                 | Purpose                                                                                                                                                                      |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Start(Config)`                     | Load a ROM, create/open its save file, validate the concrete hardware/firmware configuration, and reset the emulated hardware. Returns `false` when cartridge loading fails. |
| `Update()`                          | Execute enough CPU and subsystem clocks for one 70,224-cycle hardware frame (approximately 59.7275 Hz). The host owns scheduling.                                            |
| `GetScreenData()`                   | Return the reusable 160×144 RGB framebuffer as `Color[x, y]`. This is the emulator's internal array, not a copy.                                                             |
| `GetSuperGameBoyScreenData()`       | Return the reusable 256×224 colorized SGB composite frame, including the active game-supplied or GBZEmu fallback border.                                                     |
| `GetSoundSamples(out frameCount)`   | Swap and return reusable interleaved band-limited float amplitudes plus their valid stereo-frame count. Call once per emulation update.                                      |
| `ButtonDown(...)` / `ButtonUp(...)` | Forward Game Boy button transitions to the joypad and interrupt logic.                                                                                                       |
| `FrameRate` / `ClockRate`           | Report the selected model's host scheduling rate. DMG-B, MGB, CGB-E, and SGB2 currently use the normal Game Boy frame rate.                                                  |
| `ToggleChannel(...)`                | Enable or mute one of the four emulated audio channels.                                                                                                                      |
| `SupportsRumble` / `RumbleActive`   | Report whether the loaded cartridge has rumble hardware and its current raw motor-enable latch.                                                                              |
| `RumbleChanged`                     | Notify compatibility consumers synchronously whenever the raw MBC5 motor-enable latch changes.                                                                               |
| `RumbleStrength`                    | Report the most recently completed frame's cycle-integrated motor duty in the range `0..1`.                                                                                  |
| `RumbleStrengthUpdated`             | Notify hosts after every completed rumble-capable frame, including repeated strengths used to refresh timed haptics.                                                         |
| `Cheats`                            | Parse, add, remove, enable, and disable engine-neutral Game Genie and GameShark/Action Replay entries.                                                                       |
| `CaptureState()` / `RestoreState()` | Capture or restore a versioned snapshot bound to the running ROM, firmware, and hardware mode.                                                                               |
| `AdvanceFrames(...)`                | Execute a bounded number of hardware frames without adding wall-clock pacing.                                                                                                |
| `FastForward(...)`                  | Execute multiple hardware frames immediately while draining their core audio.                                                                                                |
| `Terminate()`                       | Flush and close file-backed cartridge RAM. Safe to call repeatedly or before `Start()`.                                                                                      |

Public constants and data types include:

- `Display.HORIZONTAL_RESOLUTION`: `160`
- `Display.VERTICAL_RESOLUTION`: `144`
- `Display.CLOCK_CYCLES_PER_FRAME`: `70224`
- `Display.FRAME_RATE`: approximately `59.7275`
- `SuperGameBoyDisplay`: `256×224`, with the `160×144` Game Boy viewport at `(48, 40)`
- `Sound.SAMPLE_RATE`: `44100`
- `JoypadButtons`: D-pad, `A`, `B`, `Select`, and `Start`
- `Color`: byte-valued `R`, `G`, and `B` components

## Build

Install the current LTS [.NET SDK](https://dotnet.microsoft.com/download), then build the SDK-style solution:

```sh
dotnet build GBZEmuLibrary.sln -c Release
```

The solution contains:

- `GBZEmuLibrary/GBZEmuLibrary.csproj`: engine-neutral `netstandard2.0` library with no package dependencies.
- `GBZEmuFrontend/GBZEmuFrontend.csproj`: `net10.0` test frontend using Raylib-cs 8.0.0.
- `GBZEmuHeadless/GBZEmuHeadless.csproj`: dependency-free `net10.0` command-line host for deterministic ROM execution and framebuffer diagnostics.
- `GBZEmuTests/GBZEmuTests.csproj`: serialized `net10.0` xUnit harness for debug tooling and test-ROM conformance.

The library assembly is written to:

```text
GBZEmuLibrary/bin/Release/netstandard2.0/GBZEmuLibrary.dll
```

Current Unity versions can reference that DLL as a managed plug-in. The core has no Unity or Raylib dependency; Raylib is confined to the separate frontend project.

## Automated testing

Run the complete suite:

```sh
dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release
```

The harness discovers every `.gb`/`.gbc` file under `GBZEmuTests/Fixtures/`, including suites from Blargg, Mooneye, dmg-acid2/cgb-acid2, SameSuite, and mealybug-tearoom-tests. It supports serial text, Blargg's `$A000` memory protocol, the Fibonacci register fingerprint used by Mooneye/SameSuite, and exact framebuffer comparison. ROM cases are interleaved across four xUnit classes so separate emulator instances can run in parallel while each shard remains serial.

Each applicable ROM is a normal test case: passing ROMs are green and failing ROMs are red in Test Explorer and `dotnet test` output. `GBZEmuTests/ExpectedRomIds.txt` locks the physical fixture inventory; a bounded execution layer also runs MGB-specific startup cases and selected shared DMG/MGB timing cases under both concrete models without duplicating ROM files. The digital APU deliberately uses the DMG-B revision for DMG-B, MGB, and SGB2 execution and CPU CGB-E behavior for CGB-E execution. SameSuite ROMs whose names and upstream sources explicitly require another silicon revision remain visible as skipped tests with that revision in the reason. `boot_hwio-dmgABCmgb` is visibly skipped for both DMG-B and MGB because the synthetic skip profile does not reproduce official firmware PPU handoff phase; ordinary correctness failures on implemented targets remain red. The complete suite therefore remains failing while conformance gaps exist; use test filters or Test Explorer selections for focused iteration. Phase 1 intentionally removes six original-SGB-only fixture IDs rather than relabeling them as SGB2 coverage; `mooneye/acceptance/boot_regs-sgb2` remains. Current test output is the authoritative source for pass/failure results. Fixture provenance, pins, licenses, and Blargg's explicit licensing ambiguity are documented in `GBZEmuTests/Fixtures/README.md`.

## Debugging API

`Emulator.Debug` exposes runtime diagnostics without adding host-framework dependencies:

- `GetCpuState()` and `GetPpuState()` return immutable snapshots including registers, flags, interrupt state, PPU mode, and cycle counters.
- `PeekByte(address)` / `PokeByte(value, address)` route through the MMU and therefore preserve hardware side effects.
- `SerialByteTransferred` captures the outgoing byte when an internal-clock transfer completes after eight emulated serial clock edges. Completion clears SC bit 7 and requests the serial interrupt. External-clock transfers remain pending because no link partner supplies clock edges.
- `Trace` provides a bounded 4,096-entry pre-fetch CPU ring buffer with instruction-range and PC-breakpoint controls.
- `RunUntilProgramCounter(address, maxFrames)` executes a bounded number of frames and stops before fetching the target instruction, which is useful for deterministic test-ROM diagnostics.
- `RequestStop()` / `Resume()` cooperatively stop inside the current frame so breakpoint state can be inspected exactly.

Debug state methods require a successfully started, non-terminated emulator. `Update()` returns immediately while stopped.

## Cheat codes

`Emulator.Cheats` accepts six- or nine-character Game Genie codes and eight-character GameShark/Action Replay codes.
Hyphens, whitespace, and hexadecimal letter case are normalized by the parser. Entries can be prepared before `Start()`
or changed while the instance is running:

```csharp
CheatEntry lives = emulator.Cheats.Add("05D-49C-E62");
CheatEntry ram = emulator.Cheats.Add("01FF00C0", enabled: false);

emulator.Cheats.SetEnabled(ram, true);
emulator.Cheats.Remove(lives);
```

Game Genie substitutes the byte returned from cartridge ROM after the active mapper bank is resolved. Nine-character
codes apply only when the mapped ROM byte matches their decoded compare value; six-character codes affect every mapped
bank at that logical address. The first enabled matching entry in insertion order wins.

GameShark/Action Replay entries write RAM once at the PPU's VBlank interrupt-request boundary. `01` codes use the
currently visible mapping, `80` through `8F` select a physical cartridge-SRAM bank without changing mapper registers,
and `90` through `97` select a physical CGB work-RAM bank without changing SVBK. Entries execute in insertion order, so
the last enabled entry targeting the same byte wins. Disabling or removing an entry stops future writes but does not
undo RAM already changed.

Cheat configuration is host policy and is intentionally outside the save-state payload and state identity. Captured RAM
contains any writes that have already occurred. Restoring a state restores that machine memory but retains the current
cheat list and enabled flags; enabled RAM codes run again at the next VBlank. This keeps rewind deterministic without
silently changing a host's active cheat selection. The decoding and application model follows the documented
[Game Boy Game Genie format](https://www.devrs.com/gb/files/gg.html) and the independently maintained
[SameBoy cheat implementation](https://github.com/LIJI32/SameBoy/blob/master/Core/cheats.c). The GameShark bank prefixes
follow the [pokecrystal code-discovery reference](https://github.com/pret/pokecrystal/wiki/Discovering-GameShark-cheat-codes).

## Time and progression control

The core exposes engine-neutral building blocks for save states, rewind, and fast-forward. Hosts remain responsible for
storage UI, button mapping, wall-clock pacing, and audio-device queue management.

```csharp
// Save and restore. ToArray()/FromArray() provide the persistence boundary.
EmulatorState state = emulator.CaptureState();
File.WriteAllBytes(path, state.ToArray());
emulator.RestoreState(EmulatorState.FromArray(File.ReadAllBytes(path)));

// Retain 10 seconds at roughly 10 checkpoints per second when Capture is called every six frames.
var rewind = new RewindBuffer(capacity: 100);
rewind.Capture(emulator);
rewind.TryRewind(emulator);

// Run ten hardware frames immediately and discard the audio they generate.
int completedFrames = emulator.FastForward(10);
```

Save-state format version 2 captures CPU, interrupts, MMU/main/work RAM, cartridge banking and RAM, MBC3 RTC phase,
timer/divider, serial, joypad, DMA, PPU/framebuffers, APU channels, and core audio buffers. A SHA-256 checksum rejects
corrupt data. State identity binds the exact ROM bytes, concrete `HardwareModel`, a firmware-vs-skip boot-kind marker,
and the active firmware hash without embedding the firmware. Built-in and byte-identical external firmware intentionally
share identity; skipped startup never shares identity with firmware startup. Restoring into another running instance is
supported only when that complete identity matches. Other format versions and mismatched identities are rejected explicitly.

`RewindBuffer` is bound to one emulator instance until cleared and bounded by checkpoint count. Its duration
and memory cost therefore depend on the host's capture cadence and each state's `SerializedLength`.
`TryRewind()` drops the newest checkpoint and restores the preceding one;
the oldest retained checkpoint is never crossed. Restoring cartridge RAM also restores the active file-backed `.sav`
contents, so rewinding past an in-game save can intentionally roll that save data back.

`AdvanceFrames(frameCount, discardAudio)` and `FastForward(frameCount)` do not introduce wall-clock timing. They execute
the same hardware-frame path as repeated `Update()` calls; `FastForward` drains each frame's core audio. A host should
also clear or reconcile any samples already queued to its audio device after restore, rewind, or a fast-forward mode
transition. When `discardAudio` is false, the existing one-frame core buffer remains bounded; it does not accumulate an
arbitrary multi-frame batch. All state and progression calls obey the existing single-instance thread-safety constraint.

## Test frontend

Run a ROM with the built-in firmware. The frontend defaults to DMG-B for DMG-only cartridges and CGB-E for CGB-compatible or CGB-only cartridges:

```sh
dotnet run --project GBZEmuFrontend -- /path/to/game.gb
```

Select a concrete model or legally obtained model-matching firmware explicitly:

```sh
dotnet run --project GBZEmuFrontend -- /path/to/game.gb --model Sgb2
dotnet run --project GBZEmuFrontend -- /path/to/game.gbc --model CgbE --bootrom /path/to/cgb_boot.bin
```

SGB2 expands the frontend to 256×224, colorizes the Game Boy image, and displays game-transferred borders; an original GBZEmu border is used until a title supplies one.

Options:

- `--rom-dir <path>`: show an in-window picker containing `.gb` and `.gbc` files from the directory instead of supplying a ROM path.
- `--model <DmgB|Mgb|CgbE|Sgb2|AgbA>`: select a concrete hardware model. DMG-B, MGB, CGB-E, and SGB2 are implemented; AGB-A is named for forward-compatible host configuration but currently fails with a clear not-implemented error. Automatic selection remains DMG-B for DMG-only cartridges and CGB-E otherwise; MGB is deliberate selection.
- `--bootrom <path>`: use external firmware for the selected model instead of its built-in image.
- `--skip-bootrom`: skip firmware execution and apply the model-specific deterministic handoff state; mutually exclusive with `--bootrom`.
- `--save-dir <path>`: save directory; defaults to the ROM directory and is created by the frontend.
- `--scale <1-10>`: integer window scale; defaults to 4 (640×576).
- `--paused`: start emulation paused before its first update.
- `--raw-frames`: disable the frontend's default adjacent-frame LCD persistence blend. Use this for exact framebuffer inspection; normal playback blends completed frames to reproduce temporal-color and transparency effects that rely on the original LCD response.
- `--raw-colors`: disable the frontend's default CGB Modern Balanced color profile and present the core's direct RGB555 expansion.

Keyboard controls: arrow keys for the D-pad, **X** for A, **Z** for B, **Enter** for Start, **Right Shift** for Select, and **Escape** to quit. Press **P** to pause or resume. While paused, tap **N** to advance one frame, or hold it for 400 ms to continue stepping at 15 frames per second. **F5** quick-saves and **F8** quick-loads; states are stored at `<save-dir>/States/<full ROM filename>.state`. Hold **R** to rewind retained checkpoints or **Tab** for 4x fast-forward. The rewind history retains 100 checkpoints captured every six emulated frames, approximately ten seconds at normal speed.

The first available controller can also play and navigate the ROM picker. Its D-pad or left stick maps to the Game Boy D-pad; east/south face buttons map to A/B; Start/Back map to Start/Select; north/west face buttons quick-save/quick-load; left/right bumpers rewind/fast-forward; and left/right stick clicks pause/frame-step. When an SGB2 game requests two or four controllers, subsequent connected gamepads feed the corresponding SGB controller slots. MBC5 rumble cartridges drive supported controller vibration, which is stopped on cartridge motor-off, controller replacement, or frontend shutdown. Keyboard and first-controller states are merged before transitions are sent to the core, so switching or disconnecting inputs releases controls cleanly.

The window title reports pause, rewind, fast-forward, and quick-state results. The frontend targets macOS, Windows, and Linux through Raylib-cs native packages. CGB color correction and frame blending are presentation-only: `Emulator.GetScreenData()` and `GBZEmuHeadless` continue exposing raw completed hardware frames.

Local development ROMs, external firmware, saves, and captures belong under the gitignored `runtime/` directory and must not be committed.

## Headless diagnostics

`GBZEmuHeadless` runs a ROM without Raylib, audio hardware, real-time pacing, or a window. It is intended for deterministic emulator debugging and build-to-build comparisons, not as a replacement for hardware reference captures.

Run 900 frames, capture every tenth frame from 650 through 900, and write PPM images plus `report.json`:

```sh
dotnet run --project GBZEmuHeadless -c Release -- \
  /path/to/demo.gbc \
  --skip-bootrom \
  --frames 900 \
  --capture-frames 650-900 \
  --capture-every 10 \
  --output runtime/captures/demo
```

Frames are numbered from 1 after each `Emulator.Update()`. `--capture-frames` is inclusive and defaults to the final frame. Input transitions are applied immediately before their numbered frame and can be repeated:

```sh
--input 120:Start:down --input 121:Start:up
```

Options:

- `--frames <count>`: total frame budget; defaults to 1.
- `--capture-frames <start[-end]>`: inclusive capture range within the frame budget.
- `--capture-every <count>`: capture cadence within that range; defaults to 1.
- `--output <path>`: report and image directory; defaults to `./headless-output`.
- `--save-dir <path>`: save directory; defaults to `<output>/saves` and is created by the host.
- `--audio-out <path>`: capture every core audio frame as deterministic little-endian interleaved stereo float32 amplitudes.
- `--model <DmgB|CgbE|Sgb2|Mgb|AgbA>`: select a concrete hardware model; defaults to DMG-B for DMG-only cartridges and CGB-E otherwise.
- `--bootrom <path>`: use external firmware for the resolved model instead of its built-in image.
- `--skip-bootrom`: use the model-specific post-firmware handoff state; mutually exclusive with `--bootrom`.
- `--input <frame:button:down|up>`: deterministic joypad transition for `Right`, `Left`, `Up`, `Down`, `A`, `B`, `Select`, or `Start`.

Each binary PPM capture is named `frame-NNNNNN.ppm`. Report format version 2 records `HardwareModel` and `BootRomSource` alongside the ROM SHA-256, capture settings, input events, frame number, framebuffer/top-row/right-column RGB hashes, CGB BG/OBJ palette RAM hashes, hashes of the first 4,048 tile-data bytes in both VRAM banks, unique RGB count, the 16 most common colors and their pixel counts, CPU registers/counters, PPU state, and `SCX`, `SCY`, `LYC`, `WX`, and `WY`. When `--audio-out` is present it also records the exact format, SHA-256, amplitude range, first non-zero sample, total sample frames, and each emulator frame's sample count. Capture paths in the report are relative to the output directory so the directory can be moved or compared as one artifact.

## Basic integration

The following shows the intended host lifecycle. A concrete hardware model is required; read [Hardware models and boot ROMs](#hardware-models-and-boot-roms) before selecting one for a cartridge.

```csharp
using System;
using GBZEmuLibrary;

Emulator emulator = new Emulator();
Emulator.Config config = new Emulator.Config(HardwareModel.CgbE)
{
    ROMPath = @"roms/game.gbc",
    SaveLocation = @"saves",
    BootRom = BootRomConfig.BuiltIn()
};

if (!emulator.Start(config))
{
    throw new InvalidOperationException("The cartridge could not be loaded.");
}

// Once per emulator.FrameRate hardware frame:
emulator.Update();

Color[,] frame = emulator.GetScreenData();
float[] audio = emulator.GetSoundSamples(out int audioFrameCount);

// From the host input callbacks:
emulator.ButtonDown(JoypadButtons.A);
emulator.ButtonUp(JoypadButtons.A);

// During host shutdown, after a successful Start():
emulator.Terminate();
```

`SaveLocation` must already exist. If it is null or empty, saves are placed in the process working directory. Choose exactly one immutable firmware source with `BootRomConfig.BuiltIn()`, `ExternalFile(path)`, `ExternalBytes(bytes)`, or `Skip()`; byte-backed configuration takes and returns private copies. An `Emulator` instance supports one successful `Start()`; create a new instance to load or restart a ROM. Separate instances may run concurrently because their hardware bus and boot-ROM state are isolated. Give concurrent battery-backed cartridges distinct save paths unless the host coordinates access to the shared save file.

Hosts can inspect ROM compatibility and the core-owned model matrix before constructing an emulator:

```csharp
CartridgeMetadata cartridge = CartridgeMetadata.Read(@"roms/game.gb");
HardwareModel model = HardwareModel.DmgB;

if (!HardwareModelMetadata.IsImplemented(model) ||
    !HardwareModelMetadata.SupportsCartridge(model, cartridge.Compatibility))
{
    // Do not offer this hardware/cartridge combination.
}
```

`CartridgeMetadata` reads only the required cartridge-header bytes and reports `DmgOnly`, `CgbCompatible`, or `CgbOnly`. `HardwareModelMetadata.ImplementedModels`, `IsImplemented(...)`, and `SupportsCartridge(...)` expose the same implementation and compatibility policy enforced by `Emulator.Start`, so hosts do not need to duplicate emulator-domain rules.

### Video

`GetScreenData()` returns the same `Color[160, 144]` array on every call. Pixels use `[x, y]` indexing, with scanline `0` at the top of the emulated display. Immediately after a successful `Start()` and before the first `Update()`, the complete host-visible framebuffer is initialized to DMG palette color 0 for DMG rendering or white for native CGB rendering. This deterministic blank is startup state, not a completed emulated frame. CGB RGB555 palette components are expanded directly to the full 8-bit range by bit replication; the core does not apply an LCD color-response profile. The core publishes a completed frame to this host-visible buffer when the PPU enters VBlank, so an `Update()` call cannot expose a mixture of scanlines from adjacent hardware frames.

SGB2 mode deliberately leaves that contract unchanged. `GetSuperGameBoyScreenData()` adds a separate reusable `Color[256, 224]` buffer. The HLE bridge reconstructs JOYP packets, applies the four screen palettes and 20×18 attribute map, implements mask/freeze state and multiplayer IDs, performs delayed `PAL_TRN`, `CHR_TRN`, `PCT_TRN`, and `ATTR_TRN` video transfers, and composites border pixels that overlap the centered Game Boy viewport.

A host should copy or convert this buffer into its own texture format before the next emulator update. Do not mutate it or consume it concurrently with `Update()`. Rendering may run at a different refresh rate, but calls to `Update()` should represent 70,224-cycle hardware frames at `Display.FRAME_RATE`; the host should use elapsed time to catch up emulation and may duplicate or skip presentation when its display rate differs. Vsync should control presentation, not emulation speed.

For Unity, `GBZEmuLibrary.Color` conflicts by name with `UnityEngine.Color`. Use a namespace alias or fully qualified name, for example:

```csharp
using EmulatedColor = GBZEmuLibrary.Color;
```

A Unity adapter can flatten `frame[x, y]` into a `Color32[]`, upload it to a 160×144 `Texture2D`, and apply nearest-neighbour filtering. Account for the host texture API's vertical origin when flattening the rows.

### Audio

Audio is generated at a fixed 44,100 Hz. `GetSoundSamples(out int sampleFrameCount)` returns a double-buffered float array with interleaved channel amplitudes reconstructed at 4× output rate through a 64-tap low-pass filter:

```text
left, right, left, right, ...
```

The buffer has capacity for 739 stereo sample frames. `sampleFrameCount` reports how many frames are valid for the completed emulation update; consume the first `sampleFrameCount * 2` floats. Values preserve fractional reconstruction and are intentionally not normalized to `-1..1`; the host applies hardware-model DC blocking, gain, device-rate conversion, and queueing. Calling `GetSoundSamples(...)` swaps and clears the producer buffer, so it should be called once after each `Update()`. The returned array is internal storage that will be cleared and reused by the next call; consume or copy it before asynchronous playback.

### Input

Forward both transitions for every mapped button:

```csharp
emulator.ButtonDown(JoypadButtons.Start);
emulator.ButtonUp(JoypadButtons.Start);
```

The core tracks active-low Game Boy joypad state and requests a joypad interrupt when an applicable button is newly pressed.

### Rumble

MBC5 rumble cartridges expose their raw motor latch through `RumbleChanged` and `RumbleActive` for compatibility. Games can pulse that latch for only a few emulated milliseconds and return it to off within one `Update()`, so hosts should drive physical haptics from `RumbleStrengthUpdated` instead. The core integrates normalized hardware cycles spent on and off during each completed frame and publishes their ratio through `RumbleStrength` and the event's `0..1` argument. `SupportsRumble` distinguishes cartridge types `0x1C` through `0x1E` from ordinary MBC5 cartridges.

```csharp
emulator.RumbleStrengthUpdated += strength =>
{
    // Scale host-configured controller motors by strength.
    // Repeated values are intentional so timed haptics can be refreshed.
};
```

Presentation-specific shaping remains a host policy: a frontend may apply a response curve, timed refresh, or a short minimum physical pulse while preserving the core's cycle-integrated strength. `Terminate()` forces `RumbleActive` and `RumbleStrength` to zero and raises one final raw off transition when necessary; repeated termination does not publish duplicates. A host should still stop its physical controller motors defensively on pause, focus loss, session replacement, and application shutdown. When emulation runs off the main thread, marshal controller API calls to the thread required by the host engine.

### Save data

`Start()` opens or creates a save file named from the complete ROM filename plus `.sav`. For example, `game.gb` produces `game.gb.sav`. The file is created even for cartridges whose parsed RAM size is zero.

Writes are file-backed and are flushed when cartridge RAM is disabled and when `Terminate()` is called. The save directory is not created automatically.

Timer-capable MBC3 cartridges append a BGB-compatible 48-byte RTC trailer after the raw RAM bytes in the same `.sav` file. Existing raw-RAM-only saves and legacy 44-byte RTC trailers remain readable; legacy trailers are normalized to 48 bytes on the next `Terminate()`. On load, elapsed UTC seconds advance the live RTC unless it was halted or the saved timestamp is in the future. The latched register snapshot remains unchanged until the game performs another latch sequence.

## Hardware models and boot ROMs

`HardwareModel` identifies a concrete physical model rather than combining hardware selection with firmware policy:

| Model  | Status      | Cartridge compatibility            | Built-in image size |
| ------ | ----------- | ---------------------------------- | ------------------- |
| `DmgB` | Implemented | DMG-only, CGB-compatible           | 256 bytes           |
| `Mgb`  | Implemented | DMG-only, CGB-compatible           | 256 bytes           |
| `CgbE` | Implemented | DMG-only, CGB-compatible, CGB-only | 2,304 bytes         |
| `Sgb2` | Implemented | DMG-only, CGB-compatible           | 256 bytes           |
| `AgbA` | Planned     | DMG-only, CGB-compatible, CGB-only | Not yet provided    |

`Emulator.Config(HardwareModel)` requires the selection up front. Undefined enum values and planned models fail with explicit validation errors before cartridge compatibility, firmware validation, or resource lookup. CGB-only cartridges are rejected on DMG-B, MGB, and SGB2. MGB uses the late monochrome hardware path with its distinct `A=$FF` startup identity; ordinary CPU, timer, serial, PPU, joypad, and digital APU behavior is shared with the evidence-backed DMG-B path. The CGB-E path retains automatic compatibility palettes for monochrome cartridges. SGB2 retains the normal DMG clock, model-specific boot handoff, and HLE command, multiplayer, palette, border, and presentation behavior; original SGB hardware is no longer a public or internal model.

`BootRomConfig` independently selects the firmware source:

- `BuiltIn()` uses the embedded open replacement image for the selected implemented model.
- `ExternalFile(path)` validates and loads a model-specific external image.
- `ExternalBytes(bytes)` takes a private copy of a model-specific image.
- `Skip()` executes no firmware and applies deterministic model- and cartridge-specific handoff state.

External firmware must match the selected model's exact image size; a 256-byte file is not inferred as DMG-B, MGB, or SGB2 by length. `BootRomSource` reports `BuiltIn`, `External`, or `Skip` for host diagnostics. Save-state identity instead distinguishes firmware execution from skip-boot and hashes the active image so byte-identical built-in and external firmware remain compatible.

Maintained source for all four built-in images lives under [`GBZEmuLibrary/BootROMs/`](GBZEmuLibrary/BootROMs/) and is licensed under the Expat/MIT license in that directory. The project-authored DMG-B and MGB images share the authentic monochrome GBZEmu logo-scroll and two-note-chime presentation; their generated images differ only in the documented final handoff accumulator (`A=$01` for DMG-B, `A=$FF` for MGB). CGB-E retains its distinct color-era presentation and compatibility-palette behavior. Normal .NET builds use checked-in generated resources and do not require RGBDS. To rebuild into an isolated temporary directory, validate exact mapped sizes, and byte-compare the results with the embedded images, install RGBDS 1.0.1 and run:

```sh
GBZEmuLibrary/BootROMs/verify.sh
```

The source README records purpose, compatibility-reference policy, provenance, and the fact that the historical upstream SameBoy revision is unknown; the local introduction commit is documented rather than replaced with an invented upstream SHA. No Nintendo code, assets, firmware bytes, or sampled audio are included. Do not commit proprietary boot ROMs, commercial ROMs, or generated save files to this repository.

## Cartridge support

The cartridge header parser recognizes these controller families. Recognition does not imply complete hardware compatibility:

| Cartridge family | Implemented path                                                                         | Important caveats                                                                                |
| ---------------- | ---------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| ROM only         | Direct ROM access and optional external RAM file.                                        | Game compatibility remains unverified.                                                           |
| MBC1             | Independent BANK1/BANK2/mode mapping, RAM banking, and MBC1M detection.                  | All 13 committed Mooneye MBC1 cases pass.                                                        |
| MBC2             | A8-gated ROM/RAM commands and persistent 512×4-bit internal RAM.                         | All 7 committed Mooneye MBC2 cases pass.                                                         |
| MBC3             | ROM/RAM banking plus cycle-driven RTC registers, latching, halt, carry, and persistence. | Broader game compatibility remains unverified.                                                   |
| MBC5             | 9-bit ROM banking, RAM-bank selection, and type-aware rumble output.                     | All 8 committed Mooneye MBC5 ROM-geometry cases pass; rumble has focused synthetic-ROM coverage. |

Mapper bank selection uses complete physical ROM banks beyond an under-declared homebrew header, including MBC3 demos that intentionally declare a smaller size than the file contains.

The header parser also detects DMG-only, CGB-compatible, and CGB-only ROM flags and includes the CGB work-RAM, VRAM, palette, speed-switch, and DMA paths.

## Architecture

```text
Host / engine
    |
    v
Emulator facade
    |
    +-- Cartridge + ExternalRAM
    +-- CPU + InterruptHandler
    +-- MMU
    |    +-- MainMemory / WorkRAM
    |    +-- Timer / DivideRegister / Joypad
    |    +-- DMAController
    |    +-- GPU
    |    +-- APU
    |
    +-- framebuffer / audio / input API
```

The CPU is the timing source. Instruction and memory operations emit clock ticks, and `Emulator.UpdateSystems()` advances the divider, timer, GPU, and APU by the corresponding cycle count. `Update()` continues processing instructions until it reaches the nominal per-frame clock budget.

The MMU precomputes an address-to-device map for cartridge space, VRAM/OAM and graphics registers, work RAM, joypad, divider/timer, audio, DMA, and fallback main memory. Each emulator owns an internal `MessageBus` that connects its interrupt requests, DMA memory access, HBlank, and VBlank notifications without process-global callbacks.

## Repository layout

```text
GBZEmuLibrary/
├── Emulator.cs                 Public host facade and concrete-model configuration
├── HardwareModel.cs            Public model enum, implementation list, and cartridge matrix
├── BootRomConfig.cs            Immutable built-in/external/skip firmware choice
├── BootROMs/                   Expat-licensed replacement firmware source and verifier
├── Cheats.cs                   Cheat entries, parsers, and lifecycle collection
├── EmulatorDebugger.cs         Debug snapshots, memory access, serial events, and stop controls
├── DebugState.cs               Immutable CPU and PPU debug snapshots
├── TraceBuffer.cs              Bounded pre-fetch CPU trace and PC breakpoint settings
├── Core/
│   ├── APU/                    Audio channels, envelopes, and sample generation
│   ├── CPU/                    CPU state, instructions, interrupts, and timing
│   ├── Cartridge/              Header parsing, MBC banking, and save RAM
│   ├── GPU/                    DMG/CGB scanline renderer and public RGB color
│   ├── Memory/                 MMU, RAM, and DMA routing
│   ├── SGB/                    SGB2 HLE packets, palettes, transfers, multiplayer, and border composition
│   ├── BootROM.cs              Model-specific built-in/external firmware loading and overlay
│   ├── Joypad.cs               Input register and joypad interrupts
│   ├── MessageBus.cs           Internal subsystem event bus
│   ├── Schemas.cs              Public constants and internal hardware map
│   └── Timer.cs                Programmable timer
└── GBZEmuLibrary.csproj        SDK-style netstandard2.0 library project
GBZEmuFrontend/                 Cross-platform Raylib-cs test host
GBZEmuHeadless/                 Deterministic command-line capture and report host
GBZEmuTests/                    xUnit debug and ROM-conformance harness
```

## Current limitations

- The conformance suite still has failures, primarily in cycle/dot-accurate PPU, DMA, interrupt, and hardware-revision behavior. All SameSuite APU cases applicable to the selected DMG-B and CGB-E targets pass; seven fixtures for other revisions remain explicit skips. `mooneye/acceptance/halt_ime0_nointr_timing` is currently deferred: resolving its remaining one-cycle discrepancy requires coordinated HALT wake, interrupt-polling, and VBlank phase modeling rather than another local timing adjustment. It remains a visible failing test instead of being suppressed. The core remains experimental while these failures remain.
- Audio output now uses band-limited float reconstruction, but it does not yet model per-channel analog DAC attack/discharge, model-specific speaker/headphone response, electrical interference, cartridge VIN input, or adaptive host/device clock matching. Those are refinement work rather than known DMG-B/CGB-E register/timer conformance failures.
- Open replacement boot-ROM data is included for DMG-B, MGB, CGB-E, and SGB2; official Nintendo firmware remains user-supplied. DMG-B and MGB skip boot restore deterministic late-monochrome CPU, DIV, serial, P1, interrupt-request, and powered-APU state. They do not yet reproduce the official firmware-exit PPU phase, so both `boot_hwio-dmgABCmgb` execution rows remain visibly skipped for that explicit boot circumstance. AGB-A is defined for stable host configuration but is not implemented and has no built-in firmware.
- SGB2 support is high-level, like SameBoy's default SGB path: it does not execute the proprietary SNES-side SGB system ROM. Border/color/attribute/mask/multiplayer commands are implemented; SNES sound program transfer, system menus, built-in Nintendo borders, and low-level SNES CPU/PPU behavior are outside this core. Original SGB hardware is intentionally unsupported.
- Cartridge behavior remains partially verified: MBC3 RTC timing and BGB-compatible persistence pass the committed Mealybug and synthetic tests, but broader game compatibility is unverified.
- Separate `Emulator` instances can run concurrently; their interrupt, MMU/DMA, HBlank, and boot-ROM state is instance-scoped.
- A single `Emulator` instance and its reused public buffers are not thread-safe. Coordinate calls to one instance and copy output buffers before consuming them asynchronously.
- The host owns real-time pacing and audio underrun/overrun handling; use `Emulator.FrameRate` rather than tying emulation speed to presentation refresh.
- STOP behavior is incomplete. Serial debug transfers are exposed through `Emulator.Debug.SerialByteTransferred`; internal-clock timing and completion interrupts are modeled, while external-clock starts remain pending. Link-cable peer exchange and externally supplied clock edges are not implemented.
- The included frontend is deliberately minimal: it has no debugger, configurable input mapping, multi-slot save-state UI, or engine-specific UI; ROM selection is limited to the configured directory picker.

## Legal and license status

Game Boy and Game Boy Color are trademarks of Nintendo. This project is not affiliated with or endorsed by Nintendo. Users are responsible for supplying software and firmware they are legally entitled to use.

The SGB2 HLE implementation and open replacement bootstrap ROMs use public factual compatibility references and independently reimplemented behavior. The firmware source and generated images carry an Expat license; see [`GBZEmuLibrary/BootROMs/LICENSE`](GBZEmuLibrary/BootROMs/LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

This repository currently has **no license file**. Unless a license is added, normal copyright restrictions apply; source availability alone does not grant permission to copy, modify, or redistribute the project.
