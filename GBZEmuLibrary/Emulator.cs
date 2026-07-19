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
        /// Defines the ROM, save location, optional boot ROM, and hardware boot mode used by <see cref="Start"/>.
        /// </summary>
        public class Config
        {
            public string ROMPath;
            public string SaveLocation;
            public string BootROMPath;
            public byte[] BootROM;
            public string SGBBootROMPath;
            public byte[] SGBBootROM;
            public string SGB2BootROMPath;
            public byte[] SGB2BootROM;
            /// <summary>
            /// Optional additional boot-ROM images; each file fills the DMG or CGB slot
            /// selected by its size. <see cref="BootROM"/>/<see cref="BootROMPath"/> load
            /// afterwards and win their slot. Missing slots use the built-in GBZEmu images
            /// unless <see cref="GBZEmuLibrary.BootMode.Skip"/> is set.
            /// </summary>
            public string[] BootROMPaths;
            public BootMode BootMode = BootMode.GBC;
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
        private byte[] _stateIdentity;
        private int _clockRate = GameBoySchema.MAX_DMG_CLOCK_CYCLES;
        private bool _apuSystemUpdateActive;
        private int _apuSystemUpdateSpeedFactor = 1;
        private int _apuClocksAdvancedThisSystemUpdate;

        /// <summary>
        /// Gets the active hardware clock rate used for host scheduling and fixed-rate audio conversion.
        /// </summary>
        public int ClockRate => _clockRate;

        /// <summary>
        /// Gets the active hardware-frame rate. SGB1 runs approximately 2.4 percent faster than a handheld DMG.
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
            _mmu = new MMU(_cartridge, _gpu, _timer, _divideRegister, _joypad, _apu, _serialRegisters, _bootROM, _messageBus);
            _cpu = new CPU(_mmu, _messageBus);
            _cpu.OnClockTick += UpdateSystems;
            _cpu.OnSpeedSwitch += HandleSpeedSwitch;
            _cartridge.RumbleStrengthUpdated += strength => RumbleStrengthUpdated?.Invoke(strength);
            Debug = new EmulatorDebugger(_cpu, _mmu, _gpu, _serialRegisters, () => _running, Update);
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

            _bootROM.Clear();

            if (config.BootROMPaths != null)
            {
                foreach (var path in config.BootROMPaths)
                {
                    _bootROM.Load(File.ReadAllBytes(path));
                }
            }

            if (config.BootROM != null)
            {
                _bootROM.Load(config.BootROM);
            }
            else if (!string.IsNullOrEmpty(config.BootROMPath))
            {
                _bootROM.Load(File.ReadAllBytes(config.BootROMPath));
            }

            if (config.SGBBootROM != null)
            {
                _bootROM.LoadSgb(config.SGBBootROM, false);
            }
            else if (!string.IsNullOrEmpty(config.SGBBootROMPath))
            {
                _bootROM.LoadSgb(File.ReadAllBytes(config.SGBBootROMPath), false);
            }

            if (config.SGB2BootROM != null)
            {
                _bootROM.LoadSgb(config.SGB2BootROM, true);
            }
            else if (!string.IsNullOrEmpty(config.SGB2BootROMPath))
            {
                _bootROM.LoadSgb(File.ReadAllBytes(config.SGB2BootROMPath), true);
            }

            // Slots without a host-supplied image fall back to the embedded GBZEmu boot ROMs.
            // This must happen before the cartridge loads because the header's custom-palette
            // lookup reads the GBC boot ROM.
            if (!config.BootMode.IsSet(BootMode.Skip))
            {
                _bootROM.EnsureDefaults();
            }

            var success = _cartridge.LoadFile(config.ROMPath, config.SaveLocation);

            if (!success)
            {
                return false;
            }

            try
            {
                var mode = _cartridge.GBCMode;
                var useBootRom = !config.BootMode.IsSet(BootMode.Skip);
                var gbcBootRom = _cartridge.GBCMode != GBCMode.NoGBC;
                var sgbModel = config.BootMode.IsSet(BootMode.SGB2) ? SgbModel.Sgb2
                    : config.BootMode.IsSet(BootMode.SGB) ? SgbModel.Sgb
                    : SgbModel.None;

                if (sgbModel != SgbModel.None)
                {
                    if (_cartridge.GBCMode == GBCMode.GBCOnly)
                    {
                        throw new ArgumentException("Trying to start a GBC-only ROM on Super Game Boy hardware");
                    }

                    mode = GBCMode.NoGBC;
                    gbcBootRom = false;
                }

                if (sgbModel == SgbModel.None && config.BootMode.IsSet(BootMode.DMG))
                {
                    if (config.BootMode.IsSet(BootMode.Force))
                    {
                        if (_cartridge.GBCMode == GBCMode.GBCOnly)
                        {
                            throw new ArgumentException("Trying to start GBC ROM with invalid Boot Mode");
                        }

                        mode = GBCMode.NoGBC;
                    }
                    else
                    {
                        mode = _cartridge.GBCMode == GBCMode.GBCOnly ? GBCMode.GBCOnly : GBCMode.NoGBC;
                        gbcBootRom = mode == GBCMode.GBCOnly;
                    }
                }
                else if (sgbModel == SgbModel.None && config.BootMode.IsSet(BootMode.GBC))
                {
                    gbcBootRom = true;

                    if (config.BootMode.IsSet(BootMode.Force))
                    {
                        mode = _cartridge.GBCMode == GBCMode.NoGBC
                            ? GBCMode.GBCCompatibility
                            : _cartridge.GBCMode;
                    }
                    else
                    {
                        mode = _cartridge.GBCMode == GBCMode.NoGBC
                            ? GBCMode.GBCCompatibility
                            : _cartridge.GBCMode;
                    }
                }

                useBootRom = useBootRom && (sgbModel == SgbModel.None
                    ? _bootROM.TrySetBootMode(gbcBootRom, config.BootMode.IsSet(BootMode.Short))
                    : _bootROM.TrySetSgbBootMode(sgbModel));

                _clockRate = sgbModel == SgbModel.Sgb
                    ? GameBoySchema.SGB_NTSC_CLOCK_CYCLES
                    : GameBoySchema.MAX_DMG_CLOCK_CYCLES;
                _mmu.Init(mode, sgbModel);
                _apu.Reset();
                _timerState.Reset(useBootRom, mode);
                _cpu.Reset(useBootRom, mode, sgbModel);
                _gpu.Reset(mode, useBootRom);
                _sgb.Reset(sgbModel, _cartridge.ROMBytes, useBootRom);

                _hasStarted = true;
                _running = true;
                _stateIdentity = ComputeStateIdentity(mode, sgbModel);
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

            var timing = new[] { _clocksThisUpdate, _clocksThisFrame };
            var payload = StateSerialization.Write(
                timing,
                _cartridge,
                _bootROM,
                _gpu,
                _sgb,
                _timerState,
                _joypad,
                _apu,
                _serialRegisters,
                _mmu,
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
            var parsed = StateEnvelope.Parse(state.Data, _stateIdentity);
            var timing = new int[2];
            var previousRumbleState = _cartridge.RumbleActive;

            StateSerialization.Read(
                parsed.Payload,
                timing,
                _cartridge,
                _bootROM,
                _gpu,
                _sgb,
                _timerState,
                _joypad,
                _apu,
                _serialRegisters,
                _mmu,
                _cpu);

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
                _cpu.Process();
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
        /// Advances all hardware from a CPU machine cycle while preserving CGB double-speed clock domains.
        /// </summary>
        private void UpdateSystems(int cycles)
        {
            // DIV, TIMA, and the serial clock are driven by the CPU clock and therefore run twice as fast in CGB
            // double-speed mode. Their independent dividers advance across the same raw CPU-clock interval.
            _apuSystemUpdateSpeedFactor = _cpu.SpeedFactor;
            _apuClocksAdvancedThisSystemUpdate = 0;
            _apuSystemUpdateActive = true;
            try
            {
                _timerState.Update(cycles);
            }
            finally
            {
                _apuSystemUpdateActive = false;
            }
            _serialRegisters.Update(cycles);

            cycles /= _apuSystemUpdateSpeedFactor;
            _clocksThisUpdate += cycles;

            _cartridge.Update(cycles);
            _gpu.Update(cycles);
            _apu.Update(cycles - _apuClocksAdvancedThisSystemUpdate);
        }

        /// <summary>
        /// Advances the APU to an exact DIV edge before clocking its frame sequencer.
        /// </summary>
        private void HandleApuClock(int rawClockOffset)
        {
            if (!_apuSystemUpdateActive)
            {
                _apu.ClockFrameSequencer();
                return;
            }

            var apuClockOffset = rawClockOffset / _apuSystemUpdateSpeedFactor;
            var clocksToEdge = apuClockOffset - _apuClocksAdvancedThisSystemUpdate;
            if (clocksToEdge > 0)
            {
                _apu.Update(clocksToEdge);
                _apuClocksAdvancedThisSystemUpdate = apuClockOffset;
            }

            _apu.ClockFrameSequencer();
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

        /// <summary>
        /// Hashes ROM and selected firmware identities without embedding copyrighted firmware bytes in state files.
        /// </summary>
        private byte[] ComputeStateIdentity(GBCMode mode, SgbModel sgbModel)
        {
            using (var sha256 = SHA256.Create())
            {
                var romHash = sha256.ComputeHash(_cartridge.ROMBytes);
                var bootHash = sha256.ComputeHash(_bootROM.Bytes ?? new byte[0]);
                var combined = new byte[romHash.Length + bootHash.Length + 2];
                Buffer.BlockCopy(romHash, 0, combined, 0, romHash.Length);
                Buffer.BlockCopy(bootHash, 0, combined, romHash.Length, bootHash.Length);
                combined[combined.Length - 2] = (byte)mode;
                combined[combined.Length - 1] = (byte)sgbModel;
                return sha256.ComputeHash(combined);
            }
        }
    }
}
