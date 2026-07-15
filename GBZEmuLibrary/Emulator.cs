using System;
using System.IO;

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
        private readonly TimerState _timerState;
        private readonly Timer _timer;
        private readonly DivideRegister _divideRegister;
        private readonly Joypad _joypad;
        private readonly APU _apu;
        private readonly SerialRegisters _serialRegisters;
        private readonly MMU _mmu;
        private readonly CPU _cpu;

        public EmulatorDebugger Debug { get; }

        private int _clocksThisUpdate;
        private int _clocksThisFrame;
        private bool _hasStarted;
        private bool _running;

        /// <summary>
        /// Creates an emulator instance with isolated hardware, boot-ROM, and internal-bus state.
        /// </summary>
        public Emulator()
        {
            _bootROM = new BootROM();
            _messageBus = new MessageBus();
            _cartridge = new Cartridge(_bootROM);
            _gpu = new GPU(_messageBus);
            _timerState = new TimerState(_messageBus);
            _timer = new Timer(_timerState);
            _divideRegister = new DivideRegister(_timerState);
            _joypad = new Joypad(_messageBus);
            _apu = new APU();
            _serialRegisters = new SerialRegisters();
            _mmu = new MMU(_cartridge, _gpu, _timer, _divideRegister, _joypad, _apu, _serialRegisters, _bootROM, _messageBus);
            _cpu = new CPU(_mmu, _messageBus);
            _cpu.OnClockTick += UpdateSystems;
            _cpu.OnSpeedSwitch += _timerState.WriteDivider;
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

                if (config.BootMode.IsSet(BootMode.DMG))
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
                else if (config.BootMode.IsSet(BootMode.GBC))
                {
                    gbcBootRom = true;

                    if (config.BootMode.IsSet(BootMode.Force))
                    {
                        mode = _cartridge.GBCMode == GBCMode.GBCOnly ? GBCMode.GBCOnly : GBCMode.GBCSupport;
                    }
                    else
                    {
                        mode = _cartridge.CustomPalette ? GBCMode.GBCSupport : _cartridge.GBCMode;
                    }
                }

                useBootRom = useBootRom && _bootROM.TrySetBootMode(gbcBootRom, config.BootMode.IsSet(BootMode.Short));

                _mmu.Init(mode);
                _apu.Reset();
                _timerState.Reset(useBootRom, mode);
                _cpu.Reset(useBootRom, mode);
                _gpu.Reset(mode != GBCMode.NoGBC);

                _hasStarted = true;
                _running = true;
                return true;
            }
            catch
            {
                _cartridge.Terminate();
                throw;
            }
        }

        public void Terminate()
        {
            if (!_running)
            {
                return;
            }

            _cartridge.Terminate();
            _running = false;
        }

        public void Update()
        {
            if (Debug.StopRequested)
            {
                return;
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
            }
        }

        public Color[,] GetScreenData()
        {
            return _gpu.GetScreenData();
        }

        public void ButtonDown(JoypadButtons button)
        {
            _joypad.ButtonDown(button);
        }

        public void ButtonUp(JoypadButtons button)
        {
            _joypad.ButtonUp(button);
        }

        public byte[] GetSoundSamples(out int sampleFrameCount)
        {
            return _apu.GetSoundSamples(out sampleFrameCount);
        }

        public void ToggleChannel(Sound.Channel channel, bool enabled)
        {
            _apu.ToggleChannel(channel, enabled);
        }

        /// <summary>
        /// Advances all hardware from a CPU machine cycle while preserving CGB double-speed clock domains.
        /// </summary>
        private void UpdateSystems(int cycles)
        {
            // DIV and TIMA are driven by the CPU clock and therefore run twice as fast in CGB double-speed mode.
            _timerState.Update(cycles);

            cycles /= _cpu.SpeedFactor;
            _clocksThisUpdate += cycles;

            _cartridge.Update(cycles);
            _gpu.Update(cycles);
            _apu.Update(cycles);
        }
    }
}
