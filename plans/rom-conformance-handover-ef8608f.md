# ROM-conformance handover after `ef8608f`

## Goal

Continue reducing the remaining emulator test-ROM failures in small, source-backed, hardware-correct batches. The next selected batch is the CH1/CH2 pulse retrigger phase exposed by SameSuite's `restart_nrx2_glitch` fixtures.

Do not weaken assertions, hide ordinary correctness failures, add checksum-specific behavior, or broaden this batch into a general APU timing rewrite.

## Repository baseline

Verify before editing:

```sh
git status --short --branch
git log -3 --oneline
```

Expected state:

- Branch: `master`
- Worktree: clean
- HEAD: `ef8608f fix(apu): model CGB envelope write timing`
- Local branch: ahead of `origin/master` by 5 commits
- Do not push unless explicitly requested.

Latest validated results at `ef8608f`:

- Release ROM inventory: **145 passed / 131 failed / 276 total**
- Non-ROM suite: **205/205 passed**
- Debug and Release solution builds passed.
- The ROM suite is intentionally red while conformance gaps remain.

The `ef8608f` batch implemented deterministic CGB active NRx2 writes, trigger-loaded envelope state, boundary locking, pending write-clock timing, and correct CGB-compatible header classification. It improved the previous clean baseline by 20 ROMs with no new failures.

## Selected next batch

Target exactly these fixtures first:

- `samesuite/apu/channel_1/channel_1_restart_nrx2_glitch`
  - xUnit class: `RomConformanceShard3Tests`
- `samesuite/apu/channel_2/channel_2_restart_nrx2_glitch`
  - xUnit class: `RomConformanceShard1Tests`

Both reproduce as failing at `ef8608f`:

```sh
dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~RomConformanceShard&DisplayName~restart_nrx2" \
  --logger "console;verbosity=normal"
```

Current result:

```text
Total: 2, Passed: 0, Failed: 2
Fibonacci fingerprint mismatch: B=42, C=42, D=42, E=42, H=42, L=42
Serial output: BBBBBB
```

Use `--list-tests` before narrower filters because xUnit truncates long theory display names.

## Why this is the best next target

This is the smallest source-backed cluster adjacent to the completed NRx2 work:

1. Both redistributable fixtures and their exact matrix oracles are already committed.
2. CH1 and CH2 share `SquareWaveGenerator` and `EnvelopeGenerator`, so one pulse-trigger behavior should cover both.
3. The upstream source isolates active pulse retriggering after a CGB NRx2 zombie write.
4. Pan Docs explicitly documents the pulse timer and duty-position behavior involved.
5. SameBoy independently models active pulse retrigger as phase-sensitive.
6. Broader remaining clusters—OAM corruption, CPU/interrupt timing, DMA, and dot-level PPU behavior—have significantly larger architectural scope.

## SameSuite source and oracle

Pinned SameSuite commit:

```text
f15645fb049a47ea235f6d2c9a033e72d8087901
```

Read the exact source:

- <https://raw.githubusercontent.com/LIJI32/SameSuite/f15645fb049a47ea235f6d2c9a033e72d8087901/apu/channel_1/channel_1_restart_nrx2_glitch.asm>
- <https://raw.githubusercontent.com/LIJI32/SameSuite/f15645fb049a47ea235f6d2c9a033e72d8087901/apu/channel_2/channel_2_restart_nrx2_glitch.asm>

The two programs are structurally identical except for channel registers and PCM12 nibble placement. Each subtest:

1. Powers the APU off and on.
2. Sets the pulse period to `0x7FC` and duty register to `0x80`.
3. Sets NRx2 to `0x80` and triggers with NRx4=`0x87`.
4. Writes NRx2=`0x28` while active to invoke the CGB zombie transition.
5. Waits 12 NOPs and retriggers with NRx4=`0x87`.
6. Reads PCM12 after offsets 0 through 15.

Expected CH1 PCM12 low-nibble matrix:

```text
02 02 02 02 02 02 02 02
02 02 02 02 02 02 02 00
```

Expected CH2 PCM12 high-nibble matrix:

```text
20 20 20 20 20 20 20 20
20 20 20 20 20 20 20 00
```

The retrigger must immediately restore programmed initial volume 2 after the zombie write, while the final offset crosses the pulse waveform timing boundary.

## Hardware evidence

Primary references:

- Pan Docs Audio Details: <https://gbdev.io/pandocs/Audio_details.html>
- Pan Docs Triggering: <https://gbdev.io/pandocs/Audio.html#triggering>

Relevant documented behavior:

- Triggering CH1/CH2 reloads the envelope's initial volume and state.
- Pulse retrigger does not reset the duty-step index; only powering the APU off resets it.
- Pulse retrigger reloads the duty-step timer.
- The low two bits of the pulse frequency timer are **not modified** by a trigger.
- PCM12 exposes CH1 in the low nibble and CH2 in the high nibble.

SameBoy is a secondary implementation reference. Its `Core/apu.c` handling for `GB_IO_NR14` and `GB_IO_NR24`:

- leaves the current pulse sample/duty index unchanged on retrigger;
- distinguishes inactive start from active retrigger;
- gives active retrigger a shorter phase-dependent delay;
- reloads current volume from NRx2 immediately;
- includes additional hardware-revision details that should not be copied unless required by the target evidence.

A temporary reference clone may be created outside the repository:

```sh
git clone --depth 1 https://github.com/LIJI32/SameBoy.git \
  /tmp/gbzemulibrary-sameboy-reference
```

Remove temporary clones when finished.

## Likely root cause

Relevant files:

- `GBZEmuLibrary/Core/APU/SquareWaveGenerator.cs`
- `GBZEmuLibrary/Core/APU/Generator.cs`
- `GBZEmuLibrary/Core/APU/EnvelopeGenerator.cs`
- `GBZEmuLibrary/Core/APU/APU.cs`
- `GBZEmuTests/ApuRegisterTests.cs`

`SquareWaveGenerator.HandleTrigger()` currently calls:

```csharp
SetFreqTimer(_originalFrequency);
```

`Generator.SetFreqTimer()` reloads `_frequency` and resets `_frequencyCount` to zero. That discards the pulse timer's existing low-bit phase on every trigger, contrary to Pan Docs.

`HandleTrigger()` already calls `RestartEnvelope()`, which correctly restores the initial volume and envelope state after `ef8608f`. Preserve that behavior. The leading hypothesis is that the remaining ROM mismatch is the pulse retrigger timer/duty boundary, not another NRx2 zombie-transition formula.

Do not assume the correct fix is to preserve all of `_frequencyCount`. First map this implementation's count-up representation to the hardware countdown and preserve only the documented low two timer bits or equivalent phase. Distinguish inactive first start from active retrigger.

## Bounded scope

In scope:

- Pulse CH1/CH2 frequency-timer phase on trigger/retrigger.
- Duty-step index preservation across retrigger.
- Immediate programmed-volume reload on retrigger after a CGB zombie write.
- Focused register/subsystem tests for active and inactive starts.
- Exact target and adjacent pulse-ROM validation.

Out of scope unless direct evidence requires it:

- General pulse frequency scheduling or all SameSuite `align`/`delay` fixtures.
- Sweep-unit redesign.
- Noise-channel timing.
- Hardware-revision-specific CGB trigger differences.
- CPU instruction timing, DMA, PPU, OAM corruption, SGB, or MBC changes.

## Focused tests to add

Prefer black-box APU/register tests through `APU.Update()` and PCM12. Avoid test-only public APIs.

Cover:

- Active CGB retrigger after an NRx2 zombie write restores programmed initial volume.
- Active pulse retrigger preserves the documented low timer phase.
- Retrigger does not reset the current duty-step index.
- CH1 and CH2 use equivalent shared behavior.
- Inactive first start remains correctly initialized.
- DMG or another unaffected path remains unchanged where the hardware behavior differs.
- Boundary assertions on the cycle before and cycle of the expected waveform transition.

Keep test magic values tied to the SameSuite setup with concise comments.

## Validation workflow

Run `dotnet` commands sequentially because output directories are shared.

1. Run focused `ApuRegisterTests` while iterating.
2. Run the exact two target ROMs.
3. Run adjacent pulse fixtures:
   - `channel_1_restart`
   - `channel_2_restart`
   - `channel_1_duty`
   - `channel_2_duty`
   - `channel_1_delay`
   - `channel_2_delay`
4. Run both Blargg sound groups:

```sh
dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~RomConformanceShard&DisplayName~blargg/dmg_sound" \
  --logger "console;verbosity=minimal"

dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~RomConformanceShard&DisplayName~blargg/cgb_sound" \
  --logger "console;verbosity=minimal"
```

1. Build the solution in both configurations:

```sh
dotnet build GBZEmuLibrary.sln -c Debug
dotnet build GBZEmuLibrary.sln -c Release
```

1. Run all non-ROM tests:

```sh
dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release --no-build \
  --filter "FullyQualifiedName!~RomConformanceShard"
```

1. Run the full ROM inventory once near the end:

```sh
dotnet test GBZEmuTests/GBZEmuTests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~RomConformanceShard" \
  --logger "console;verbosity=minimal"
```

1. Compare against **145 passed / 131 failed / 276 total** at `ef8608f`:
   - identify every newly passing fixture ID;
   - confirm no fixture that passed at `ef8608f` became red;
   - do not hide expected existing failures.
1. Run workspace diagnostics and `git diff --check`.

## Review and commit

For the implementation review, explicitly request the latest available Opus 4.8 model:

```text
model: claude-opus-4-8
```

Use `reviewScope` over the changed paths. Verify the scheduler accepted the exact model and did not fall back. If 4.8 is unavailable, report that explicitly rather than silently substituting 4.6. If the review workflow completes without output, record missing review evidence instead of describing the review as clean.

Ask the reviewer to check:

- count-up versus hardware-countdown phase mapping;
- low-two-bit timer preservation;
- active versus inactive trigger semantics;
- duty-index preservation;
- CH1/CH2 equivalence;
- DMG/CGB separation;
- focused-test boundary quality;
- regressions to sweep and ordinary envelope timing.

Commit only when the bounded diff and validation are clean. Use a Conventional Commit message based on the actual behavior change. Do not push.

## Project constraints

- Preserve `netstandard2.0` and the engine-neutral core.
- CPU clocks remain the only timing source.
- Do not add Unity, Raylib, UI, device, or wall-clock dependencies to the core.
- Avoid allocations, LINQ, reflection, logging, and exceptions in hot APU paths.
- Preserve public video, audio, input, save, boot, and lifecycle contracts.
- Do not commit commercial ROMs, proprietary firmware, user saves, generated captures, or temporary clones.
- Keep smoke-test notes concise and checklist-based.
