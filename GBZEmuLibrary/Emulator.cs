﻿namespace GBZEmuLibrary
{
    public class Emulator
    {
        public enum BootAnimMode
        {
            NoBoot = 0x01,
            Normal = 0x02,
            Short = 0x03,
            NoAnim = 0x00
        }

        public class Config
        {
            public string ROMPath;
            public BootAnimMode BootAnimMode = BootAnimMode.Normal;
            public bool ForceDMG = false;
        }

        private const int CLOCKS_PER_CYCLE     = GameBoySchema.MAX_DMG_CLOCK_CYCLES / GameBoySchema.TARGET_FRAMERATE;

        private Cartridge      _cartridge;
        private GPU            _gpu;
        private Timer          _timer;
        private DivideRegister _divideRegister;
        private Joypad         _joypad;
        private APU            _apu;
        private MMU            _mmu;
        private CPU            _cpu;

        private int _clocksThisUpdate;
        private int _clocksThisFrame;

        public void Init()
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
            var success = _cartridge.LoadFile(config.ROMPath, config.ForceDMG);

            BootROM.SetBootMode(_cartridge.GBCMode != GBCMode.NoGBC, (byte)config.BootAnimMode);

            if (success)
            {
                _cpu.Reset(config.BootAnimMode != BootAnimMode.NoBoot, _cartridge.GBCMode);
                _gpu.Init(_cartridge.GBCMode);
                _mmu.Init(_cartridge.GBCMode);
            }

            return success;
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