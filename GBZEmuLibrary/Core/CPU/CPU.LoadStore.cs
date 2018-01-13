using System;
using InsSchema = GBZEmuLibrary.InstructionSchema;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        private void LoadImmediate(out byte reg)
        {
            reg = ReadByte(_pc++);
        }

        private void LoadImmediate(out ushort reg)
        {
            reg = Read16Bit(_pc);
            _pc += 2;
        }

        private void LoadFromAddress(out byte reg, int address)
        {
            reg = ReadByte(address);
        }

        private void LoadFromImmediateAddress(out byte reg)
        {
            var address = ReadByte(_pc++);
            reg = ReadByte(0xFF00 | address);
        }

        private void LoadFromImmediate16BitAddress(out byte reg)
        {
            var address = Read16Bit(_pc);
            _pc += 2;
            reg = ReadByte(address);
        }

        private void Load(ref byte reg, byte data)
        {
            reg = data;
        }

        private void Load(ref ushort reg, ushort data)
        {
            reg = data;
            IncrementClock();
        }

        private void LoadHLSPSpecial()
        {
            var n = ReadByte(_pc++);

            _registers.HL = (ushort)((_sp.P + (sbyte)n) & 0xFFFF);

            SetFlag(InsSchema.FLAG_C, (_sp.Lo + n) > 0xFF);
            SetFlag(InsSchema.FLAG_H, ((_sp.P & 0x0F) + (n & 0x0F)) > 0x0F);

            SetFlag(InsSchema.FLAG_Z, false);
            SetFlag(InsSchema.FLAG_N, false);

            IncrementClock();
        }

        private void StoreRegister(byte reg, int address)
        {
            WriteByte(reg, address);
        }

        private void StoreRegisterToImmediateAddress(byte reg)
        {
            var address = ReadByte(_pc++);
            StoreRegister(reg, 0xFF00 | address);
        }

        private void StoreSPToImmediateAddress()
        {
            var address = Read16Bit(_pc);
            _pc += 2;

            StoreRegister(_sp.Lo, address);
            StoreRegister(_sp.Hi, address + 1);
        }

        private void StoreRegisterToImmediateAddress16Bit(byte reg)
        {
            var address = Read16Bit(_pc);
            _pc += 2;

            StoreRegister(reg, address);
        }

        private void Push(ushort data)
        {
            WriteByte((byte)(data >> 8), --_sp.P);
            WriteByte((byte)(data & 0xFF), --_sp.P);
            IncrementClock();
        }

        private void Pop(out ushort reg)
        {
            reg = Read16Bit(_sp.P);
            _sp.P += 2;
        }
    }
}