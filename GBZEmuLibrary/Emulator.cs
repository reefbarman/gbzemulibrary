using System;

namespace GBZEmuLibrary
{
    public class Emulator
    {
        public class Config
        {
            public string   ROMPath;
            public string   SaveLocation;
            public BootMode BootMode = BootMode.GBC;
        }

        private const int CLOCKS_PER_CYCLE = GameBoySchema.MAX_DMG_CLOCK_CYCLES / GameBoySchema.TARGET_FRAMERATE;

        private readonly Cartridge      _cartridge;
        private readonly GPU            _gpu;
        private readonly Timer          _timer;
        private readonly DivideRegister _divideRegister;
        private readonly Joypad         _joypad;
        private readonly APU            _apu;
        private readonly MMU            _mmu;
        private readonly CPU            _cpu;

        private int _clocksThisUpdate;
        private int _clocksThisFrame;

        public Emulator()
        {
            _cartridge       =  new Cartridge();
            _gpu             =  new GPU();
            _timer           =  new Timer();
            _divideRegister  =  new DivideRegister();
            _joypad          =  new Joypad();
            _apu             =  new APU();
            _mmu             =  new MMU(_cartridge, _gpu, _timer, _divideRegister, _joypad, _apu);
            _cpu             =  new CPU(_mmu);
            _cpu.OnClockTick += UpdateSystems;
        }

        public bool Start(Config config)
        {
            var success = _cartridge.LoadFile(config.ROMPath, config.SaveLocation);

            var mode       = _cartridge.GBCMode;
            var useBootRom = !config.BootMode.IsSet(BootMode.Skip);
            var gbcBootRom = _cartridge.GBCMode != GBCMode.NoGBC;

            if (useBootRom)
            {
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
                        mode       = _cartridge.GBCMode == GBCMode.GBCOnly ? GBCMode.GBCOnly : GBCMode.NoGBC;
                        gbcBootRom = mode == GBCMode.GBCOnly;
                    }
                }
                else if (config.BootMode.IsSet(BootMode.GBC))
                {
                    gbcBootRom = true;

                    if (!config.BootMode.IsSet(BootMode.Force))
                    {
                        mode = _cartridge.CustomPalette ? GBCMode.GBCSupport : _cartridge.GBCMode;
                    }
                }
            }

            BootROM.SetBootMode(gbcBootRom, config.BootMode.IsSet(BootMode.Skip));

            if (success)
            {
                _cpu.Reset(useBootRom, mode);
                _gpu.Reset(mode != GBCMode.NoGBC);
                _mmu.Init(mode);
            }

            return success;
        }

        public void Terminate()
        {
            _cartridge.Terminate();
        }

        public void Update()
        {
            do
            {
                _clocksThisUpdate = 0;

                _cpu.Process();
                _cpu.UpdateInterrupts();

                _clocksThisFrame += _clocksThisUpdate;
            } while (_clocksThisFrame < (CLOCKS_PER_CYCLE * _cpu.SpeedFactor));

            _clocksThisFrame -= (CLOCKS_PER_CYCLE * _cpu.SpeedFactor);
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

        public byte[] GetSoundSamples()
        {
            return _apu.GetSoundSamples();
        }

        public void ToggleChannel(Sound.Channel channel, bool enabled)
        {
            _apu.ToggleChannel(channel, enabled);
        }

        private void UpdateSystems(int cycles)
        {
            _clocksThisUpdate += cycles;
            _divideRegister.Update(cycles);
            _timer.Update(cycles);
            _gpu.Update(cycles);
            _apu.Update(cycles / _cpu.SpeedFactor);
        }
    }
}
