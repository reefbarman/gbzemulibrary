using System;
using InsSchema = GBZEmuLibrary.InstructionSchema;

namespace GBZEmuLibrary
{
    internal enum Interrupts
    {
        VBlank,
        LCD,
        Timer,
        Serial,
        Joypad
    }

    internal class InterruptHandler
    {
        private const int VBLANK_SERVICE_ROUTINE = 0x40;
        private const int LCD_SERVICE_ROUTINE    = 0x48;
        private const int TIMER_SERVICE_ROUTINE  = 0x50;
        private const int SERIAL_SERVICE_ROUTINE = 0x58;
        private const int JOYPAD_SERVICE_ROUTINE = 0x60;

        public Action PushProgramCounter = null;
        public Action<ushort, bool> UpdateProgramCounter = null;
        public Action<int> IncrementClock = null;

        public bool InterruptsEnabled { get; set; }
        public bool Halted { get; set; }

        private MMU _mmu;

        public InterruptHandler(MMU mmu)
        {
            _mmu = mmu;
        }

        public void RequestInterrupt(Interrupts interrupt)
        {
            UpdateRegister(interrupt, true);

            var register = _mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
            var enabled  = _mmu.ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START);

            if (Helpers.TestBit(register, (int)interrupt) && Helpers.TestBit(enabled, (int)interrupt) && Halted)
            {
                Halted = false;
            }
        }

        public void Update()
        {
            if (InterruptsEnabled)
            {
                var register = _mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
                var enabled = _mmu.ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
                if (register > 0)
                {
                    for (var i = 0; i <= (int)Interrupts.Joypad; i++)
                    {
                        if (Helpers.TestBit(register, i) && Helpers.TestBit(enabled, i))
                        {
                            ServiceInterrupt(i);
                            return;
                        }
                    }
                }
            }
        }

        private void ServiceInterrupt(int interrupt)
        {
            InterruptsEnabled = false;

            UpdateRegister((Interrupts)interrupt, false);

            PushProgramCounter?.Invoke();

            switch ((Interrupts)interrupt)
            {
                case Interrupts.VBlank:
                    UpdateProgramCounter?.Invoke(VBLANK_SERVICE_ROUTINE, false);
                    break;
                case Interrupts.LCD:
                    UpdateProgramCounter?.Invoke(LCD_SERVICE_ROUTINE, false);
                    break;
                case Interrupts.Timer:
                    UpdateProgramCounter?.Invoke(TIMER_SERVICE_ROUTINE, false);
                    break;
                case Interrupts.Serial:
                    UpdateProgramCounter?.Invoke(SERIAL_SERVICE_ROUTINE, false);
                    break;
                case Interrupts.Joypad:
                    UpdateProgramCounter?.Invoke(JOYPAD_SERVICE_ROUTINE, true);
                    break;
                default:
                    throw new IndexOutOfRangeException();
            }

            IncrementClock?.Invoke(2);
        }

        private void UpdateRegister(Interrupts interrupt, bool value)
        {
            var register = _mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
            Helpers.SetBit(ref register, (int)interrupt, value);
            _mmu.WriteByte(register, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        }
    }
}
