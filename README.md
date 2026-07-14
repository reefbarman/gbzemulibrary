# GBZEmuLibrary

GBZEmuLibrary is an embeddable Game Boy and Game Boy Color emulator core written in C#. It provides CPU, graphics, audio, cartridge, memory, timer, DMA, and joypad emulation without owning a window, renderer, audio device, or input system.

The library is intended to sit behind a host such as Unity or another C# engine: the host advances emulation, uploads the framebuffer to its own texture, submits audio to its own mixer, and forwards input events.

> **Project status:** experimental and compatibility-driven. The core contains substantial DMG/CGB functionality, but the repository has no automated test suite or published compatibility matrix. See [Current limitations](#current-limitations) before integrating it.

## Highlights

- Engine-neutral `Emulator` facade with no Unity or other third-party dependencies.
- LR35902 CPU instruction dispatch, including the CB-prefixed instruction set, interrupts, HALT handling, and CGB double-speed state.
- Scanline-based DMG/CGB graphics with backgrounds, windows, sprites, palettes, VRAM banking, and DMA paths.
- Four Game Boy audio channels: two square channels, the programmable wave channel, and the noise channel.
- ROM-only, MBC1, MBC2, MBC3, and MBC5 cartridge header/banking paths.
- File-backed external cartridge RAM.
- Public RGB framebuffer, stereo sample buffer, and joypad API designed for host-engine adapters.
- Targets `netstandard2.0` for compatibility with current Unity versions and modern .NET hosts.
- Includes a small cross-platform Raylib-cs frontend for interactive video, audio, and input testing.

## Host API

Most integrations only need `GBZEmuLibrary.Emulator`:

| API                                 | Purpose                                                                                                                                   |
| ----------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `Start(Config)`                     | Load a ROM, create/open its save file, select a boot mode, and reset the emulated hardware. Returns `false` when cartridge loading fails. |
| `Update()`                          | Execute enough CPU and subsystem clocks for one 70,224-cycle hardware frame (approximately 59.7275 Hz). The host owns scheduling.         |
| `GetScreenData()`                   | Return the reusable 160×144 RGB framebuffer as `Color[x, y]`. This is the emulator's internal array, not a copy.                          |
| `GetSoundSamples(out frameCount)`   | Swap and return the reusable interleaved stereo byte buffer plus its valid stereo-frame count. Call once per emulation update.            |
| `ButtonDown(...)` / `ButtonUp(...)` | Forward Game Boy button transitions to the joypad and interrupt logic.                                                                    |
| `ToggleChannel(...)`                | Enable or mute one of the four emulated audio channels.                                                                                   |
| `Terminate()`                       | Flush and close file-backed cartridge RAM. Safe to call repeatedly or before `Start()`.                                                   |

Public constants and data types include:

- `Display.HORIZONTAL_RESOLUTION`: `160`
- `Display.VERTICAL_RESOLUTION`: `144`
- `Display.CLOCK_CYCLES_PER_FRAME`: `70224`
- `Display.FRAME_RATE`: approximately `59.7275`
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

The library assembly is written to:

```text
GBZEmuLibrary/bin/Release/netstandard2.0/GBZEmuLibrary.dll
```

Current Unity versions can reference that DLL as a managed plug-in. The core has no Unity or Raylib dependency; Raylib is confined to the separate frontend project.

## Test frontend

Run a ROM without firmware to use the emulator's post-boot initialization path:

```sh
dotnet run --project GBZEmuFrontend -- /path/to/game.gb
```

Run with a legally obtained 256-byte DMG or 2304-byte CGB boot ROM:

```sh
dotnet run --project GBZEmuFrontend -- /path/to/game.gbc --bootrom /path/to/cgb_boot.bin
```

Options:

- `--rom-dir <path>`: show an in-window picker containing `.gb` and `.gbc` files from the directory instead of supplying a ROM path.
- `--bootrom <path>`: firmware image; omit to use `BootMode.Skip`.
- `--save-dir <path>`: save directory; defaults to the ROM directory and is created by the frontend.
- `--scale <1-10>`: integer window scale; defaults to 4 (640×576).
- `--dmg`: request and force DMG mode. Use this with a DMG boot ROM; it rejects CGB-only cartridges.
- `--paused`: start emulation paused before its first update.

Controls: arrow keys for the D-pad, **X** for A, **Z** for B, **Enter** for Start, **Right Shift** for Select, and **Escape** to quit. In the ROM picker, use **Up/Down** to choose a ROM and **Enter** to load it. Press **P** to pause or resume emulation. While paused, tap **N** to advance one emulation frame, or hold it for 400 ms to continue stepping at 15 frames per second. The window title includes `[PAUSED]` while frame-step mode is active. The frontend targets macOS, Windows, and Linux through Raylib-cs native packages.

For local development, `.vscode/launch.json` contains F5 profiles for assets under the gitignored `runtime/` directory. `Frontend: ROM Picker (paused)` lists ROMs from `runtime/roms`, loads `runtime/bios/gbc_bios.bin`, and starts the selected ROM paused. ROM-specific profiles remain available for direct launches.

## Basic integration

The following shows the intended host lifecycle. The boot-mode choice in the example is specifically for a CGB-compatible cartridge in the current checkout; read [Boot ROMs and boot modes](#boot-roms-and-boot-modes) before using it with other cartridges.

```csharp
using System;
using GBZEmuLibrary;

Emulator emulator = new Emulator();
Emulator.Config config = new Emulator.Config
{
    ROMPath = @"roms/game.gbc",
    SaveLocation = @"saves",
    BootROMPath = @"firmware/cgb_boot.bin", // Optional; alternatively set BootROM to byte[]
    BootMode = BootMode.GBC
};

if (!emulator.Start(config))
{
    throw new InvalidOperationException("The cartridge could not be loaded.");
}

// Once per 59.7275 Hz hardware frame:
emulator.Update();

Color[,] frame = emulator.GetScreenData();
byte[] audio = emulator.GetSoundSamples(out int audioFrameCount);

// From the host input callbacks:
emulator.ButtonDown(JoypadButtons.A);
emulator.ButtonUp(JoypadButtons.A);

// During host shutdown, after a successful Start():
emulator.Terminate();
```

`SaveLocation` must already exist. If it is null or empty, saves are placed in the process working directory. `BootROM` bytes take precedence over `BootROMPath` when both are set. An `Emulator` instance supports one successful `Start()`; create a new instance to load or restart a ROM.

### Video

`GetScreenData()` returns the same `Color[160, 144]` array on every call. Pixels use `[x, y]` indexing, with scanline `0` at the top of the emulated display. Each component is an 8-bit RGB value.

A host should copy or convert this buffer into its own texture format before the next emulator update. Do not mutate it or consume it concurrently while `Update()` is writing scanlines. Rendering may run at a different refresh rate, but calls to `Update()` should represent 70,224-cycle hardware frames at `Display.FRAME_RATE`; the host should use elapsed time to catch up emulation and may duplicate or skip presentation when its display rate differs. Vsync should control presentation, not emulation speed.

For Unity, `GBZEmuLibrary.Color` conflicts by name with `UnityEngine.Color`. Use a namespace alias or fully qualified name, for example:

```csharp
using EmulatedColor = GBZEmuLibrary.Color;
```

A Unity adapter can flatten `frame[x, y]` into a `Color32[]`, upload it to a 160×144 `Texture2D`, and apply nearest-neighbour filtering. Account for the host texture API's vertical origin when flattening the rows.

### Audio

Audio is generated at a fixed 44,100 Hz. `GetSoundSamples(out int sampleFrameCount)` returns a double-buffered byte array with interleaved channel amplitudes:

```text
left, right, left, right, ...
```

The buffer has capacity for 739 stereo sample frames. `sampleFrameCount` reports how many frames are valid for the completed emulation update; consume the first `sampleFrameCount * 2` bytes. These bytes are emulator channel amplitudes, not a ready-to-play Unity `float` buffer. The host is responsible for normalization/conversion, buffering, and submitting samples at the required cadence. Calling `GetSoundSamples(...)` swaps and clears the producer buffer, so it should be called once after each `Update()`. The returned array is internal storage that will be cleared and reused by the next `GetSoundSamples(...)` call; consume or copy it before then, especially when handing audio to an asynchronous host API.

### Input

Forward both transitions for every mapped button:

```csharp
emulator.ButtonDown(JoypadButtons.Start);
emulator.ButtonUp(JoypadButtons.Start);
```

The core tracks active-low Game Boy joypad state and requests a joypad interrupt when an applicable button is newly pressed.

### Save data

`Start()` opens or creates a save file named from the complete ROM filename plus `.sav`. For example, `game.gb` produces `game.gb.sav`. The file is created even for cartridges whose parsed RAM size is zero.

Writes are file-backed and are flushed when cartridge RAM is disabled and when `Terminate()` is called. The save directory is not created automatically.

## Boot ROMs and boot modes

`BootMode` is a flags enum:

| Flag    | Intent                                                                   | Behavior                                                                                    |
| ------- | ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| `DMG`   | Request original Game Boy startup behavior.                              | Selects a supplied 256-byte DMG image when booting.                                         |
| `GBC`   | Request Game Boy Color startup behavior.                                 | The default `Config` value; selects a supplied 2304-byte CGB image when booting.            |
| `Skip`  | Begin from post-boot CPU/register state instead of executing a boot ROM. | Does not require firmware.                                                                  |
| `Force` | Force the requested hardware mode where possible.                        | Forcing DMG mode rejects CGB-only cartridges.                                               |
| `Short` | Use the shortened DMG startup animation.                                 | Applies the existing byte patch to a private copy of a supplied DMG image; ignored for CGB. |

The library does not distribute firmware. Supply a 256-byte DMG or 2304-byte CGB image through `Emulator.Config.BootROMPath` or `Emulator.Config.BootROM`. An invalid image length throws `ArgumentException`. If boot execution is requested but no compatible image is supplied, `Start()` safely falls back to the post-boot initialization path rather than reading missing firmware.

Do not commit proprietary boot ROMs, commercial ROMs, or generated save files to this repository.

## Cartridge support

The cartridge header parser recognizes these controller families. Recognition does not imply complete hardware compatibility:

| Cartridge family | Implemented path                                   | Important caveats                                                                                                  |
| ---------------- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| ROM only         | Direct ROM access and optional external RAM file.  | Compatibility is not covered by automated tests.                                                                   |
| MBC1             | ROM/RAM bank switching and external RAM enable.    | Hardware edge cases remain compatibility-dependent.                                                                |
| MBC2             | ROM bank selection and RAM-enable commands.        | MBC2's built-in nibble RAM is not modeled, so cartridge RAM is nonfunctional.                                      |
| MBC3             | ROM bank switching and bank-0 external RAM access. | Banked external RAM and real-time-clock register selection, latching, and persistence are not implemented.         |
| MBC5             | ROM bank switching and bank-0 external RAM access. | Banked external RAM is nonfunctional, and the 2 MiB ROM buffer prevents larger MBC5 images from loading correctly. |

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

The MMU precomputes an address-to-device map for cartridge space, VRAM/OAM and graphics registers, work RAM, joypad, divider/timer, audio, DMA, and fallback main memory. An internal `MessageBus` connects interrupt requests, DMA memory access, and HBlank notifications.

## Repository layout

```text
GBZEmuLibrary/
├── Emulator.cs                 Public host facade
├── Core/
│   ├── APU/                    Audio channels, envelopes, and sample generation
│   ├── CPU/                    CPU state, instructions, interrupts, and timing
│   ├── Cartridge/              Header parsing, MBC banking, and save RAM
│   ├── GPU/                    DMG/CGB scanline renderer and public RGB color
│   ├── Memory/                 MMU, RAM, and DMA routing
│   ├── BootMode.cs             Public boot-mode flags
│   ├── BootROM.cs              Runtime host-supplied boot-ROM storage
│   ├── Joypad.cs               Input register and joypad interrupts
│   ├── MessageBus.cs           Internal subsystem event bus
│   ├── Schemas.cs              Public constants and internal hardware map
│   └── Timer.cs                Programmable timer
└── GBZEmuLibrary.csproj        SDK-style netstandard2.0 library project
GBZEmuFrontend/                 Cross-platform Raylib-cs test host
```

## Current limitations

- No automated tests, CI configuration, conformance results, or game compatibility matrix are included.
- Boot-ROM data is not included; hosts must provide firmware at runtime or use skip-boot initialization.
- Cartridge behavior is partial: notably no MBC2 RAM, no MBC3 RTC, nonfunctional MBC3/MBC5 external-RAM bank selection, and a 2 MiB ROM-image cap.
- The internal `MessageBus` is a static singleton. Multiple `Emulator` instances overwrite callbacks and accumulate subscriptions, so only one live instance should be used per process.
- The public buffers and emulation state are not thread-safe.
- The host owns real-time pacing and audio underrun/overrun handling; the core advances one approximately 59.7275 Hz hardware frame per `Update()`.
- STOP behavior is incomplete, and serial/link-cable emulation is not implemented (the serial test convention writes characters to standard output).
- The included frontend is deliberately minimal: no debugger UI, rewind, save states, configurable input mapping, or engine-specific adapter is included; ROM selection is limited to the configured directory picker.

## Legal and license status

Game Boy and Game Boy Color are trademarks of Nintendo. This project is not affiliated with or endorsed by Nintendo. Users are responsible for supplying software and firmware they are legally entitled to use.

This repository currently has **no license file**. Unless a license is added, normal copyright restrictions apply; source availability alone does not grant permission to copy, modify, or redistribute the project.
