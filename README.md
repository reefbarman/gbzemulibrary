# GBZEmuLibrary

GBZEmuLibrary is an embeddable Game Boy and Game Boy Color emulator core written in C#. It provides CPU, graphics, audio, cartridge, memory, timer, DMA, and joypad emulation without owning a window, renderer, audio device, or input system.

The library is intended to sit behind a host such as Unity or another C# engine: the host advances emulation, uploads the framebuffer to its own texture, submits audio to its own mixer, and forwards input events.

> **Project status:** experimental and compatibility-driven. The core contains substantial DMG/CGB functionality, but the repository has no automated test suite or published compatibility matrix. See [Current limitations](#current-limitations), especially the boot-ROM shim caveat, before integrating it.

## Highlights

- Engine-neutral `Emulator` facade with no Unity or other third-party dependencies.
- LR35902 CPU instruction dispatch, including the CB-prefixed instruction set, interrupts, HALT handling, and CGB double-speed state.
- Scanline-based DMG/CGB graphics with backgrounds, windows, sprites, palettes, VRAM banking, and DMA paths.
- Four Game Boy audio channels: two square channels, the programmable wave channel, and the noise channel.
- ROM-only, MBC1, MBC2, MBC3, and MBC5 cartridge header/banking paths.
- File-backed external cartridge RAM.
- Public RGB framebuffer, stereo sample buffer, and joypad API designed for host-engine adapters.
- Targets .NET Framework 3.5 APIs for compatibility with older C# engine runtimes.

## Host API

Most integrations only need `GBZEmuLibrary.Emulator`:

| API                                 | Purpose                                                                                                                                   |
| ----------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `Start(Config)`                     | Load a ROM, create/open its save file, select a boot mode, and reset the emulated hardware. Returns `false` when cartridge loading fails. |
| `Update()`                          | Execute enough CPU and subsystem clocks for one nominal 60 Hz emulation tick. The host owns scheduling and catch-up behavior.             |
| `GetScreenData()`                   | Return the reusable 160×144 RGB framebuffer as `Color[x, y]`. This is the emulator's internal array, not a copy.                          |
| `GetSoundSamples()`                 | Swap and return the current fixed-rate, interleaved stereo byte buffer. Call once per emulation update.                                   |
| `ButtonDown(...)` / `ButtonUp(...)` | Forward Game Boy button transitions to the joypad and interrupt logic.                                                                    |
| `ToggleChannel(...)`                | Enable or mute one of the four emulated audio channels.                                                                                   |
| `Terminate()`                       | Flush and close file-backed cartridge RAM. Call it after every successful `Start()` before discarding the emulator.                       |

Public constants and data types include:

- `Display.HORIZONTAL_RESOLUTION`: `160`
- `Display.VERTICAL_RESOLUTION`: `144`
- `Sound.SAMPLE_RATE`: `44100`
- `JoypadButtons`: D-pad, `A`, `B`, `Select`, and `Start`
- `Color`: byte-valued `R`, `G`, and `B` components

## Build

The solution contains one classic, non-SDK-style C# library project:

- Solution: `GBZEmuLibrary.sln`
- Project: `GBZEmuLibrary/GBZEmuLibrary.csproj`
- Target: .NET Framework 3.5, Any CPU
- NuGet dependencies: none

Build with Visual Studio/MSBuild and a .NET Framework 3.5 targeting pack:

```powershell
msbuild GBZEmuLibrary.sln /p:Configuration=Release
```

The resulting assembly is written to:

```text
GBZEmuLibrary/bin/Release/GBZEmuLibrary.dll
```

A compatible Mono `xbuild` installation can also build this project on platforms where the .NET Framework 3.5 reference assemblies are available. `dotnet build` is not the canonical build path for this legacy project format and target.

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
    BootMode = BootMode.GBC | BootMode.Skip
};

if (!emulator.Start(config))
{
    throw new InvalidOperationException("The cartridge could not be loaded.");
}

// Once per nominal 60 Hz emulation tick:
emulator.Update();

Color[,] frame = emulator.GetScreenData();
byte[] audio = emulator.GetSoundSamples();

// From the host input callbacks:
emulator.ButtonDown(JoypadButtons.A);
emulator.ButtonUp(JoypadButtons.A);

// During host shutdown, after a successful Start():
emulator.Terminate();
```

`SaveLocation` must already exist. If it is null or empty, saves are placed in the process working directory.

### Video

`GetScreenData()` returns the same `Color[160, 144]` array on every call. Pixels use `[x, y]` indexing, with scanline `0` at the top of the emulated display. Each component is an 8-bit RGB value.

A host should copy or convert this buffer into its own texture format before the next emulator update. Do not mutate it or consume it concurrently while `Update()` is writing scanlines. Rendering may run at a different refresh rate, but calls to `Update()` should represent 60 Hz emulation ticks; the host must decide how to catch up or skip presentation when its frame rate differs.

For Unity, `GBZEmuLibrary.Color` conflicts by name with `UnityEngine.Color`. Use a namespace alias or fully qualified name, for example:

```csharp
using EmulatedColor = GBZEmuLibrary.Color;
```

A Unity adapter can flatten `frame[x, y]` into a `Color32[]`, upload it to a 160×144 `Texture2D`, and apply nearest-neighbour filtering. Account for the host texture API's vertical origin when flattening the rows.

### Audio

Audio is generated at a fixed 44,100 Hz. `GetSoundSamples()` returns a double-buffered byte array with interleaved channel amplitudes:

```text
left, right, left, right, ...
```

The current buffer length is 1,472 bytes (736 stereo sample frames), approximately one 60 Hz emulation update. These bytes are emulator channel amplitudes, not a ready-to-play Unity `float` buffer. The host is responsible for normalization/conversion, buffering, and submitting samples at the required cadence. Calling `GetSoundSamples()` swaps and clears the producer buffer, so it should normally be called once after each `Update()`. The returned array is internal storage that will be cleared and reused by the next `GetSoundSamples()` call; consume or copy it before then, especially when handing audio to an asynchronous host API.

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

| Flag    | Intent                                                                   | Current behavior                                       |
| ------- | ------------------------------------------------------------------------ | ------------------------------------------------------ |
| `DMG`   | Request original Game Boy startup behavior.                              | Used by `Start()` when a boot sequence is enabled.     |
| `GBC`   | Request Game Boy Color startup behavior.                                 | The default `Config` value.                            |
| `Skip`  | Begin from post-boot CPU/register state instead of executing a boot ROM. | Also reaches the current quick-boot selection code.    |
| `Force` | Force the requested hardware mode where possible.                        | Forcing DMG mode rejects CGB-only cartridges.          |
| `Short` | Intended for a shortened boot animation.                                 | Declared but not currently read by `Emulator.Start()`. |

`GBZEmuLibrary/Core/BootROM.cs` is currently a distribution shim: its DMG, short-DMG, and CGB byte arrays are empty. Consequently, the normal boot-ROM paths are not self-contained. In addition, the existing DMG skip path indexes the empty short-DMG array. In the current checkout, a CGB-compatible cartridge with `BootMode.GBC | BootMode.Skip` avoids boot-ROM reads; DMG startup requires a code change or a lawful host-supplied boot-ROM mechanism.

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
│   ├── BootROM.cs              Empty boot-ROM distribution shim
│   ├── Joypad.cs               Input register and joypad interrupts
│   ├── MessageBus.cs           Internal subsystem event bus
│   ├── Schemas.cs              Public constants and internal hardware map
│   └── Timer.cs                Programmable timer
└── GBZEmuLibrary.csproj        .NET Framework 3.5 library project
```

## Current limitations

- No automated tests, CI configuration, conformance results, or game compatibility matrix are included.
- Boot-ROM data is not included, and the DMG skip-boot path is incomplete as described above.
- Cartridge behavior is partial: notably no MBC2 RAM, no MBC3 RTC, nonfunctional MBC3/MBC5 external-RAM bank selection, and a 2 MiB ROM-image cap.
- The internal `MessageBus` is a static singleton. Multiple `Emulator` instances overwrite callbacks and accumulate subscriptions, so only one live instance should be used per process.
- The public buffers and emulation state are not thread-safe.
- Frame pacing uses a nominal 60 Hz integer budget rather than exact hardware timing; the host owns real-time pacing and underrun/overrun handling.
- STOP behavior is incomplete, and serial/link-cable emulation is not implemented (the serial test convention writes characters to standard output).
- There is no frontend, debugger UI, rewind, save-state system, ROM browser, input mapper, or engine-specific adapter in this repository.

## Legal and license status

Game Boy and Game Boy Color are trademarks of Nintendo. This project is not affiliated with or endorsed by Nintendo. Users are responsible for supplying software and firmware they are legally entitled to use.

This repository currently has **no license file**. Unless a license is added, normal copyright restrictions apply; source availability alone does not grant permission to copy, modify, or redistribute the project.
