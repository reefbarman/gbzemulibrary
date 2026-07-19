using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Provides fixed, switchable, and mirrored work RAM plus the CGB SVBK register.
    /// </summary>
    internal class WorkRAM : IMemoryUnit
    {
        private const int MAX_NUM_RAM_BANKS = 8;
        private const byte SVBK_UNUSED_BITS = 0xF8;

        private readonly byte[] _memory = new byte[MemorySchema.MAX_WORK_RAM_BANK_SIZE * MAX_NUM_RAM_BANKS];

        private int _ramBank = 1;

        private GBCMode _mode;

        public void Init(GBCMode mode)
        {
            _mode = mode;
        }

        public bool CanReadWriteByte(int address)
        {
            if (address >= MemorySchema.WORK_RAM_START && address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                return true;
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                return true;
            }

            return false;
        }

        public byte ReadByte(int address)
        {
            if (address < MemorySchema.WORK_RAM_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START];
            }

            if (address < MemorySchema.WORK_RAM_SWITCHABLE_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START + GetBankOffset()];
            }

            if (address < MemorySchema.ECHO_RAM_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_OFFSET];
            }

            if (address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_SWITCHABLE_OFFSET + GetBankOffset()];
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                return _mode == GBCMode.NoGBC
                    ? (byte)0xFF
                    : (byte)(SVBK_UNUSED_BITS | _ramBank);
            }

            throw new IndexOutOfRangeException();
        }

        public void WriteByte(byte data, int address)
        {
            if (address < MemorySchema.WORK_RAM_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START] = data;
                return;
            }

            if (address < MemorySchema.WORK_RAM_SWITCHABLE_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START + GetBankOffset()] = data;
                return;
            }

            if (address < MemorySchema.ECHO_RAM_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_OFFSET] = data;
                return;
            }

            if (address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_SWITCHABLE_OFFSET + GetBankOffset()] = data;
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                _ramBank = Helpers.GetBits(data, 3);
            }
        }

        /// <summary>
        /// Gets whether an address resolves through the switchable CGB work-RAM window or its echo.
        /// </summary>
        internal bool IsSwitchableAddress(int address)
        {
            return address >= MemorySchema.WORK_RAM_SWITCHABLE_START && address < MemorySchema.WORK_RAM_SWITCHABLE_END ||
                   address >= MemorySchema.ECHO_RAM_SWITCHABLE_START && address < MemorySchema.ECHO_RAM_SWITCHABLE_END;
        }

        /// <summary>
        /// Writes a physical CGB work-RAM bank without changing the guest-visible SVBK register.
        /// Bank zero has the same effective mapping as bank one, matching SVBK hardware behavior.
        /// </summary>
        internal void WriteBankedByte(byte data, int address, int bank)
        {
            if (!IsSwitchableAddress(address))
            {
                throw new ArgumentOutOfRangeException(nameof(address));
            }

            if (bank < 0 || bank >= MAX_NUM_RAM_BANKS)
            {
                throw new ArgumentOutOfRangeException(nameof(bank));
            }

            var canonicalAddress = address >= MemorySchema.ECHO_RAM_SWITCHABLE_START
                ? address - MemorySchema.WORK_RAM_ECHO_SWITCHABLE_OFFSET
                : address;
            var effectiveBank = _mode == GBCMode.NoGBC ? 1 : Math.Max(bank, 1);
            var bankOffset = (effectiveBank - 1) * MemorySchema.MAX_WORK_RAM_BANK_SIZE;
            _memory[canonicalAddress - MemorySchema.WORK_RAM_START + bankOffset] = data;
        }

        private int GetBankOffset()
        {
            var effectiveBank = Math.Max(_ramBank, 1);
            return _mode != GBCMode.NoGBC ? (effectiveBank - 1) * MemorySchema.MAX_WORK_RAM_BANK_SIZE : 0;
        }
    }
}
