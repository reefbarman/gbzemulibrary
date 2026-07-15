using System;

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

    /// <summary>
    /// Owns the Game Boy interrupt registers' priority, request, enable, and HALT wake behavior.
    /// </summary>
    internal sealed class InterruptHandler
    {
        private const int VBLANK_SERVICE_ROUTINE = 0x40;
        private const int LCD_SERVICE_ROUTINE = 0x48;
        private const int TIMER_SERVICE_ROUTINE = 0x50;
        private const int SERIAL_SERVICE_ROUTINE = 0x58;
        private const int JOYPAD_SERVICE_ROUTINE = 0x60;
        private const int INTERRUPT_MASK = 0x1F;

        public bool InterruptsEnabled { get; set; }
        public bool Halted { get; set; }

        private readonly MMU _mmu;

        /// <summary>
        /// Creates an interrupt controller backed by the MMU's IF and IE registers.
        /// </summary>
        public InterruptHandler(MMU mmu)
        {
            _mmu = mmu;
        }

        /// <summary>
        /// Sets an IF request bit and wakes HALT when that interrupt is enabled.
        /// </summary>
        public void RequestInterrupt(Interrupts interrupt)
        {
            UpdateRegister(interrupt, true);

            var requested = _mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
            var enabled = _mmu.ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START);

            if (Helpers.TestBit(requested, (int)interrupt) &&
                Helpers.TestBit(enabled, (int)interrupt) &&
                Halted)
            {
                Halted = false;
            }
        }

        /// <summary>
        /// Returns whether an enabled interrupt is currently requested.
        /// </summary>
        public bool HasPendingInterrupt()
        {
            return PendingInterruptBits() != 0;
        }

        /// <summary>
        /// Returns the highest-priority pending interrupt, or -1 when dispatch was cancelled.
        /// </summary>
        public int GetHighestPriorityPendingInterrupt()
        {
            var pending = PendingInterruptBits();
            for (var interrupt = 0; interrupt <= (int)Interrupts.Joypad; interrupt++)
            {
                if (Helpers.TestBit(pending, interrupt))
                {
                    return interrupt;
                }
            }

            return -1;
        }

        /// <summary>
        /// Clears the selected interrupt's IF request bit after dispatch priority is resolved.
        /// </summary>
        public void ClearInterruptRequest(int interrupt)
        {
            UpdateRegister((Interrupts)interrupt, false);
        }

        /// <summary>
        /// Returns the service vector for the selected interrupt.
        /// </summary>
        public ushort GetServiceRoutine(int interrupt)
        {
            switch ((Interrupts)interrupt)
            {
                case Interrupts.VBlank:
                    return VBLANK_SERVICE_ROUTINE;
                case Interrupts.LCD:
                    return LCD_SERVICE_ROUTINE;
                case Interrupts.Timer:
                    return TIMER_SERVICE_ROUTINE;
                case Interrupts.Serial:
                    return SERIAL_SERVICE_ROUTINE;
                case Interrupts.Joypad:
                    return JOYPAD_SERVICE_ROUTINE;
                default:
                    throw new ArgumentOutOfRangeException(nameof(interrupt));
            }
        }

        /// <summary>
        /// Updates one IF request bit while preserving requests from other interrupt sources.
        /// </summary>
        private void UpdateRegister(Interrupts interrupt, bool value)
        {
            var register = _mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
            Helpers.SetBit(ref register, (int)interrupt, value);
            _mmu.WriteByte(register, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        }

        /// <summary>
        /// Returns the five hardware interrupt bits that are both requested in IF and enabled in IE.
        /// </summary>
        private byte PendingInterruptBits()
        {
            var requested = _mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
            var enabled = _mmu.ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
            return (byte)(requested & enabled & INTERRUPT_MASK);
        }
    }
}
