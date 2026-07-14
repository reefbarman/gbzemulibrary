using System;
using System.IO;

namespace GBZEmuLibrary
{
    public class Emulator
    {
        public class Config
        {
            public string ROMPath;
            public string SaveLocation;
            public string BootROMPath;
            public byte[] BootROM;
            public BootMode BootMode = BootMode.GBC;
        }


        private readonly Cartridge _cartridge;
        private readonly GPU _gpu;
        private readonly Timer _timer;
        private readonly DivideRegister _divideRegister;
        private readonly Joypad _joypad;
        private readonly APU _apu;
        private readonly MMU _mmu;
        private readonly CPU _cpu;

        private int _clocksThisUpdate;
        private int _clocksThisFrame;
        private bool _hasStarted;
        private bool _running;

        public Emulator()
        {
            _cartridge = new Cartridge();
            _gpu = new GPU();
            _timer = new Timer();
            _divideRegister = new DivideRegister();
            _joypad = new Joypad();
            _apu = new APU();
            _mmu = new MMU(_cartridge, _gpu, _timer, _divideRegister, _joypad, _apu);
            _cpu = new CPU(_mmu);
            _cpu.OnClockTick += UpdateSystems;
        }

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

            BootROM.Clear();

            if (config.BootROM != null)
            {
                BootROM.Load(config.BootROM);
            }
            else if (!string.IsNullOrEmpty(config.BootROMPath))
            {
                BootROM.Load(File.ReadAllBytes(config.BootROMPath));
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

                useBootRom = useBootRom && BootROM.TrySetBootMode(gbcBootRom, config.BootMode.IsSet(BootMode.Short));

                _apu.Reset();
                _cpu.Reset(useBootRom, mode);
                _gpu.Reset(mode != GBCMode.NoGBC);
                _mmu.Init(mode);

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
            do
            {
                _clocksThisUpdate = 0;

                _cpu.Process();
                _cpu.UpdateInterrupts();

                _clocksThisFrame += _clocksThisUpdate;
            } while (_clocksThisFrame < Display.CLOCK_CYCLES_PER_FRAME);

            _clocksThisFrame -= Display.CLOCK_CYCLES_PER_FRAME;
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

        private void UpdateSystems(int cycles)
        {
            cycles /= _cpu.SpeedFactor;

            _clocksThisUpdate += cycles;

            _divideRegister.Update(cycles);
            _timer.Update(cycles);
            _gpu.Update(cycles);
            _apu.Update(cycles);
        }
    }
}
