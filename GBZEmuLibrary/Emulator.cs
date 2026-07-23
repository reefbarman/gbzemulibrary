using System;
using System.IO;
using System.Security.Cryptography;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Coordinates the emulated CPU and hardware subsystems and exposes the host-facing emulator lifecycle.
    /// </summary>
    public class Emulator
    {
        /// <summary>
        /// Defines the ROM, save location, concrete hardware model, and firmware choice used by <see cref="Start"/>.
        /// </summary>
        public sealed class Config
        {
            /// <summary>
            /// Creates a startup configuration for one required concrete hardware model.
            /// </summary>
            public Config(HardwareModel hardwareModel)
            {
                HardwareModel = hardwareModel;
            }

            public string ROMPath;
            public string SaveLocation;
            public BootRomConfig BootRom = BootRomConfig.BuiltIn();

            /// <summary>
            /// Gets the concrete physical hardware model selected by the host.
            /// </summary>
            public HardwareModel HardwareModel { get; }
        }


        private readonly Cartridge _cartridge;
        private readonly BootROM _bootROM;
        private readonly MessageBus _messageBus;
        private readonly GPU _gpu;
        private readonly SgbSystem _sgb;
        private readonly TimerState _timerState;
        private readonly Timer _timer;
        private readonly DivideRegister _divideRegister;
        private readonly Joypad _joypad;
        private readonly APU _apu;
        private readonly SerialRegisters _serialRegisters;
        private readonly MMU _mmu;
        private readonly CPU _cpu;

        public EmulatorDebugger Debug { get; }

        /// <summary>
        /// Gets the host-owned cheat collection for this emulator instance. Entries can be prepared before startup
        /// and changed while running, but hosts must not mutate the collection concurrently with emulation.
        /// </summary>
        public CheatCollection Cheats { get; }

        /// <summary>
        /// Gets whether the loaded cartridge declares an MBC5 rumble motor.
        /// </summary>
        public bool SupportsRumble => _cartridge.HasRumble;

        /// <summary>
        /// Gets the current emulated cartridge motor-enable state.
        /// </summary>
        public bool RumbleActive => _cartridge.RumbleActive;

        /// <summary>
        /// Gets the fraction of normalized hardware cycles for which the rumble motor was enabled during the most
        /// recently completed frame.
        /// </summary>
        public float RumbleStrength => _cartridge.RumbleStrength;

        /// <summary>
        /// Raised synchronously when a rumble cartridge changes its motor-enable state.
        /// </summary>
        public event Action<bool> RumbleChanged
        {
            add
            {
                _cartridge.RumbleChanged += value;
            }
            remove
            {
                _cartridge.RumbleChanged -= value;
            }
        }

        /// <summary>
        /// Raised after each completed rumble-capable hardware frame with its cycle-integrated motor duty in the
        /// inclusive range zero through one.
        /// </summary>
        public event Action<float> RumbleStrengthUpdated;

        private int _clocksThisUpdate;
        private int _clocksThisFrame;
        private bool _hasStarted;
        private bool _running;
        private HardwareModel _hardwareModel;
        private byte[] _stateIdentity;
        private int _clockRate = GameBoySchema.MAX_DMG_CLOCK_CYCLES;
        private readonly ClockCoordinator _clockCoordinator = new ClockCoordinator();
        private bool _rawClockUpdateActive;
        private bool _baseClockEmittedThisRawClock;
        private bool _apuAdvancedThisRawClock;
        [SaveStateIgnore]
        private bool _cpuProcessActive;
        [SaveStateIgnore]
        private ITimingObserver _timingObserver;

        /// <summary>
        /// Gets the active hardware clock rate used for host scheduling and fixed-rate audio conversion.
        /// </summary>
        public int ClockRate => _clockRate;

        /// <summary>
        /// Gets the active hardware-frame rate.
        /// </summary>
        public double FrameRate => (double)_clockRate / Display.CLOCK_CYCLES_PER_FRAME;

        /// <summary>
        /// Creates an emulator instance with isolated hardware, boot-ROM, and internal-bus state.
        /// </summary>
        public Emulator()
        {
            _bootROM = new BootROM();
            _messageBus = new MessageBus();
            _cartridge = new Cartridge(_bootROM);
            _gpu = new GPU(_messageBus);
            _sgb = new SgbSystem(_gpu);
            _timerState = new TimerState(_messageBus);
            _timer = new Timer(_timerState);
            _divideRegister = new DivideRegister(_timerState);
            _joypad = new Joypad(_messageBus, _sgb);
            _apu = new APU();
            _apu.IsApuDividerHigh = () => _timerState.ApuDividerHigh;
            _timerState.OnApuClock = HandleApuClock;
            _timerState.OnDividerWrite = _apu.HandleDividerWrite;
            _serialRegisters = new SerialRegisters(_messageBus);
            Cheats = new CheatCollection();
            _mmu = new MMU(_cartridge, _gpu, _timer, _divideRegister, _joypad, _apu, _serialRegisters, _bootROM, Cheats, _messageBus);
            _cpu = new CPU(_mmu, _messageBus);
            _cpu.OnClockTick += UpdateSystems;
            _cpu.OnSpeedSwitch += HandleSpeedSwitch;
            _cartridge.RumbleStrengthUpdated += strength => RumbleStrengthUpdated?.Invoke(strength);
            Debug = new EmulatorDebugger(_cpu, _mmu, _gpu, _serialRegisters, () => _running, Update);
        }

        /// <summary>
        /// Installs an optional internal observer for current CPU and subsystem timing boundaries.
        /// </summary>
        internal void SetTimingObserver(ITimingObserver timingObserver)
        {
            _timingObserver = timingObserver;
            _messageBus.SetTimingObserver(timingObserver);
            _cpu.SetTimingObserver(timingObserver);
        }

        /// <summary>
        /// Reports the fixed MMU owner for focused internal address-map characterization tests.
        /// </summary>
        internal MemoryAddressOwner GetAddressOwnerForTesting(int address)
        {
            return _mmu.GetAddressOwner(address);
        }

        /// <summary>
        /// Loads the configured cartridge and initializes the emulated hardware for its single supported run.
        /// </summary>
        public bool Start(Config config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (_hasStarted)
            {
                throw new InvalidOperationException("An Emulator instance can only be started once. Create a new instance to load another ROM.");
            }

            ValidateHardwareModel(config.HardwareModel);

            if (!File.Exists(config.ROMPath))
            {
                return false;
            }

            var compatibility = CartridgeMetadata.Read(config.ROMPath).Compatibility;
            if (!HardwareModelMetadata.SupportsCartridge(config.HardwareModel, compatibility))
            {
                throw new ArgumentException(
                    $"Hardware model {config.HardwareModel} does not support {compatibility} cartridges.",
                    nameof(config));
            }

            BootROM.ValidateConfig(config.BootRom);
            _bootROM.Load(config.HardwareModel, config.BootRom);

            var success = _cartridge.LoadFile(config.ROMPath, config.SaveLocation);
            if (!success)
            {
                return false;
            }

            try
            {
                _hardwareModel = config.HardwareModel;
                var startupProfile = _hardwareModel == HardwareModel.AgbA
                    ? HardwareStartupProfile.ResolveAgbA(_cartridge.Header)
                    : null;
                var mode = startupProfile?.ExecutionMode ?? ResolveExecutionMode(_hardwareModel, _cartridge.GBCMode);
                var sgbModel = _hardwareModel == HardwareModel.Sgb2 ? SgbModel.Sgb2 : SgbModel.None;
                var useBootRom = config.BootRom.Source != BootRomSource.Skip;

                _clockRate = GameBoySchema.MAX_DMG_CLOCK_CYCLES;
                _clockCoordinator.Reset();
                _rawClockUpdateActive = false;
                _baseClockEmittedThisRawClock = false;
                _apuAdvancedThisRawClock = false;
                _mmu.Init(mode, _hardwareModel);
                _apu.Reset();
                _timerState.Reset(useBootRom, mode);
                _mmu.Reset(useBootRom);
                _gpu.Reset(mode, useBootRom);
                if (!useBootRom && startupProfile != null)
                {
                    _mmu.ApplyStartupProfile(startupProfile);
                    if (startupProfile.InstallCompatibilityPalettes)
                    {
                        _gpu.InstallCompatibilityPalettes(CompatibilityPaletteSelector.Select(_cartridge.Header));
                    }
                }
                _cpu.Reset(useBootRom, _hardwareModel, mode, startupProfile);
                _sgb.Reset(sgbModel, _cartridge.ROMBytes, useBootRom);

                _hasStarted = true;
                _running = true;
                _stateIdentity = ComputeStateIdentity(_hardwareModel, useBootRom);
                return true;
            }
            catch
            {
                _cartridge.Terminate();
                throw;
            }
        }

        /// <summary>
        /// Stops cartridge output and flushes persistent cartridge state. Safe to call repeatedly.
        /// </summary>
        public void Terminate()
        {
            if (!_running)
            {
                return;
            }

            try
            {
                _cartridge.Terminate();
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>
        /// Advances emulated hardware until one 70,224-cycle frame budget completes.
        /// </summary>
        public void Update()
        {
            UpdateFrame();
        }

        /// <summary>
        /// Advances up to the requested number of hardware frames without applying wall-clock pacing.
        /// </summary>
        /// <param name="frameCount">Maximum completed frames to emulate.</param>
        /// <param name="discardAudio">Whether to drain each completed frame's audio instead of leaving it for playback.</param>
        /// <returns>The number of completed frames; debugger stops can return fewer.</returns>
        public int AdvanceFrames(int frameCount, bool discardAudio = false)
        {
            if (frameCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            EnsureRunning();

            var completed = 0;
            while (completed < frameCount && UpdateFrame())
            {
                completed++;
                if (discardAudio)
                {
                    _apu.GetSoundSamples(out _);
                }
            }

            return completed;
        }

        /// <summary>
        /// Advances multiple hardware frames while discarding their audio, suitable for host-controlled fast-forward.
        /// </summary>
        public int FastForward(int frameCount)
        {
            return AdvanceFrames(frameCount, true);
        }

        /// <summary>
        /// Captures all mutable machine state in a versioned payload bound to the current ROM and boot-ROM setup.
        /// </summary>
        public EmulatorState CaptureState()
        {
            EnsureRunning();

            EnsureStateOperationBoundary();

            var timing = new[] { _clocksThisUpdate, _clocksThisFrame };
            var payload = StateSerialization.Write(
                timing,
                _clockCoordinator,
                _cartridge,
                _bootROM,
                _gpu,
                _sgb,
                _timerState,
                _joypad,
                _apu,
                _serialRegisters,
                _mmu,
                _mmu.DmaController,
                _mmu.CompatibilityModeRegisters,
                _cpu);
            return StateEnvelope.Create(_stateIdentity, payload);
        }

        /// <summary>
        /// Restores a compatible snapshot into this running instance without restarting the cartridge.
        /// </summary>
        public void RestoreState(EmulatorState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            EnsureRunning();
            EnsureStateOperationBoundary();
            var parsed = StateEnvelope.Parse(state.Data, _stateIdentity);
            var timing = new int[2];
            var previousRumbleState = _cartridge.RumbleActive;

            StateSerialization.Read(
                parsed.Payload,
                timing,
                _clockCoordinator,
                _cartridge,
                _bootROM,
                _gpu,
                _sgb,
                _timerState,
                _joypad,
                _apu,
                _serialRegisters,
                _mmu,
                _mmu.DmaController,
                _mmu.CompatibilityModeRegisters,
                _cpu);

            _rawClockUpdateActive = false;
            _baseClockEmittedThisRawClock = false;
            _apuAdvancedThisRawClock = false;
            _clocksThisUpdate = timing[0];
            _clocksThisFrame = timing[1];
            _cartridge.PublishRestoredRumbleState(previousRumbleState);
        }

        /// <summary>
        /// Executes until one frame budget completes, preserving partial progress when debugger execution stops.
        /// </summary>
        private bool UpdateFrame()
        {
            if (Debug.StopRequested)
            {
                return false;
            }

            do
            {
                _clocksThisUpdate = 0;
                _cpuProcessActive = true;
                try
                {
                    _cpu.Process();
                }
                finally
                {
                    _cpuProcessActive = false;
                }

                _clocksThisFrame += _clocksThisUpdate;
            } while (_clocksThisFrame < Display.CLOCK_CYCLES_PER_FRAME && !Debug.StopRequested);

            if (_clocksThisFrame >= Display.CLOCK_CYCLES_PER_FRAME)
            {
                _clocksThisFrame -= Display.CLOCK_CYCLES_PER_FRAME;
                _sgb.FrameCompleted();
                return true;
            }

            return false;
        }

        public Color[,] GetScreenData()
        {
            return _gpu.GetScreenData();
        }

        /// <summary>
        /// Gets whether this emulator is running an SGB or SGB2 hardware profile.
        /// </summary>
        public bool IsSuperGameBoy => _sgb.Enabled;

        /// <summary>
        /// Returns the reusable 256x224 SGB composite framebuffer, including colorization and border.
        /// </summary>
        public Color[,] GetSuperGameBoyScreenData()
        {
            return _sgb.GetScreenData();
        }

        /// <summary>
        /// Gets the number of controller slots requested by the running SGB title.
        /// </summary>
        public int SuperGameBoyPlayerCount => _sgb.Enabled ? _sgb.PlayerCount : 1;

        public void ButtonDown(JoypadButtons button)
        {
            _joypad.ButtonDown(button);
        }

        /// <summary>
        /// Presses a logical Game Boy button for an SGB controller slot from zero through three.
        /// </summary>
        public void ButtonDown(JoypadButtons button, int player)
        {
            _joypad.ButtonDown(button, player);
        }

        public void ButtonUp(JoypadButtons button)
        {
            _joypad.ButtonUp(button);
        }

        /// <summary>
        /// Releases a logical Game Boy button for an SGB controller slot from zero through three.
        /// </summary>
        public void ButtonUp(JoypadButtons button, int player)
        {
            _joypad.ButtonUp(button, player);
        }

        public float[] GetSoundSamples(out int sampleFrameCount)
        {
            return _apu.GetSoundSamples(out sampleFrameCount);
        }

        public void ToggleChannel(Sound.Channel channel, bool enabled)
        {
            _apu.ToggleChannel(channel, enabled);
        }

        /// <summary>
        /// Resets DIV for a completed speed switch and selects the divider bit that keeps DIV-APU at 512 Hz.
        /// </summary>
        private void HandleSpeedSwitch()
        {
            _timerState.WriteDivider();
            _timerState.SetDoubleSpeed(_cpu.SpeedFactor == 2);
        }

        /// <summary>
        /// Advances every hardware clock domain by one raw CPU clock in deterministic software precedence order.
        /// </summary>
        private void UpdateSystems(int cycles)
        {
            if (cycles != 1)
            {
                throw new ArgumentOutOfRangeException(nameof(cycles));
            }

            var advance = _clockCoordinator.AdvanceRawClock(_cpu.SpeedFactor);
            if (advance.TState == 1)
            {
                _timerState.BeginCpuMachineCycle();
            }

            ObserveTiming(new TimingEvent(
                TimingEventKind.SystemUpdateStarted,
                value: (byte)advance.TState,
                clocks: 1));

            // The per-clock APU flags are meaningful only while _rawClockUpdateActive is true.
            _baseClockEmittedThisRawClock = advance.EmitsBaseClock;
            _apuAdvancedThisRawClock = false;
            _rawClockUpdateActive = true;
            try
            {
                _timerState.AdvanceRawClock();
            }
            finally
            {
                _rawClockUpdateActive = false;
            }
            ObserveTiming(new TimingEvent(TimingEventKind.TimerUpdateCompleted, clocks: 1));

            _serialRegisters.Update(1);
            ObserveTiming(new TimingEvent(TimingEventKind.SerialUpdateCompleted, clocks: 1));

            _mmu.AdvanceDmaRawClock();
            ObserveTiming(new TimingEvent(TimingEventKind.DmaUpdateCompleted, clocks: 1));

            if (advance.EmitsBaseClock)
            {
                AdvanceBaseClock();
            }

            ObserveTiming(new TimingEvent(
                TimingEventKind.SystemUpdateCompleted,
                value: (byte)advance.TState,
                clocks: advance.EmitsBaseClock ? 1 : 0));
        }

        /// <summary>
        /// Advances cartridge, PPU, APU, and frame accounting by one base-speed clock.
        /// </summary>
        private void AdvanceBaseClock()
        {
            _clocksThisUpdate++;

            _cartridge.Update(1);
            ObserveTiming(new TimingEvent(TimingEventKind.CartridgeUpdateCompleted, clocks: 1));

            _gpu.Update(1);
            ObserveTiming(new TimingEvent(TimingEventKind.GpuUpdateCompleted, clocks: 1));

            if (!_apuAdvancedThisRawClock)
            {
                _apu.Update(1);
                ObserveTiming(new TimingEvent(TimingEventKind.ApuUpdateCompleted, clocks: 1));
            }
        }

        /// <summary>
        /// Applies a DIV-APU edge at its exact raw-clock boundary without integer-offset truncation.
        /// </summary>
        private void HandleApuClock(int rawClockOffset)
        {
            if (_rawClockUpdateActive && _baseClockEmittedThisRawClock && !_apuAdvancedThisRawClock)
            {
                _apu.Update(1);
                _apuAdvancedThisRawClock = true;
                ObserveTiming(new TimingEvent(TimingEventKind.ApuUpdateCompleted, clocks: 1));
            }

            _apu.ClockFrameSequencer();
            ObserveTiming(new TimingEvent(
                TimingEventKind.ApuFrameSequencerClocked,
                clocks: rawClockOffset));
        }

        private void ObserveTiming(in TimingEvent timingEvent)
        {
            _timingObserver?.Observe(in timingEvent);
        }

        /// <summary>
        /// Rejects capture or restore while a CPU instruction, HALT cycle, or interrupt sequence can still mutate state.
        /// </summary>
        private void EnsureStateOperationBoundary()
        {
            if (_cpuProcessActive || !_clockCoordinator.IsMachineCycleAligned)
            {
                throw new InvalidOperationException("Save states can only be captured or restored between CPU operations.");
            }
        }

        /// <summary>
        /// Rejects time-control operations when hardware or cartridge storage is not live.
        /// </summary>
        private void EnsureRunning()
        {
            if (!_running)
            {
                throw new InvalidOperationException("This operation requires a running emulator.");
            }
        }

        private static void ValidateHardwareModel(HardwareModel model)
        {
            if (!Enum.IsDefined(typeof(HardwareModel), model))
            {
                throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown hardware model.");
            }

            if (!HardwareModelMetadata.IsImplemented(model))
            {
                throw new NotSupportedException($"Hardware model {model} is not implemented.");
            }
        }

        private static GBCMode ResolveExecutionMode(HardwareModel model, GBCMode cartridgeMode)
        {
            if (model == HardwareModel.CgbE || model == HardwareModel.AgbA)
            {
                return cartridgeMode == GBCMode.NoGBC ? GBCMode.GBCCompatibility : cartridgeMode;
            }

            return GBCMode.NoGBC;
        }

        /// <summary>
        /// Hashes ROM, concrete hardware, boot kind, and active firmware identity without storing firmware in states.
        /// </summary>
        private byte[] ComputeStateIdentity(HardwareModel model, bool usingBootRom)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = new MemoryStream())
            {
                var romHash = sha256.ComputeHash(_cartridge.ROMBytes);
                stream.Write(romHash, 0, romHash.Length);
                stream.WriteByte((byte)model);
                stream.WriteByte(usingBootRom ? (byte)1 : (byte)0);

                if (usingBootRom)
                {
                    var bootHash = sha256.ComputeHash(_bootROM.Bytes);
                    stream.Write(bootHash, 0, bootHash.Length);
                }

                return sha256.ComputeHash(stream.ToArray());
            }
        }
    }
}
