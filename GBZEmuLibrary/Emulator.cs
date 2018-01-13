using System;
using System.Drawing;

namespace GBZEmuLibrary
{
    public class Emulator
    {
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

        public bool Start(string romFile, bool usingBios)
        {
            _cpu.Reset(usingBios);

            return _cartridge.LoadFile(romFile);
        }

        public void Update()
        {
            do
            {
                _clocksThisUpdate = 0;

                _cpu.Process();
                _cpu.UpdateInterrupts();

                _clocksThisFrame += _clocksThisUpdate;
            } while (_clocksThisFrame < CLOCKS_PER_CYCLE);

            _clocksThisFrame -= CLOCKS_PER_CYCLE;
        }

        public Color[,] GetScreenData()
        {
            return _gpu.GetScreenData();
        }

        public int GetScreenWidth()
        {
            return GPU.HORIZONTAL_RESOLUTION;
        }

        public int GetScreenHeight()
        {
            return GPU.VERTICAL_RESOLUTION;
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

        private void UpdateSystems(int cycles)
        {
            _clocksThisUpdate += cycles;
            _divideRegister.Update(cycles);
            _timer.Update(cycles);
            _gpu.Update(cycles);
            _apu.Update(cycles);
        }
    }
}