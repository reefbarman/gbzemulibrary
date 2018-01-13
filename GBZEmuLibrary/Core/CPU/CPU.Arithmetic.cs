using System;
using InsSet = GBZEmuLibrary.InstructionSet;
using InsCBSet = GBZEmuLibrary.CBInstructionSet;
using InsSchema = GBZEmuLibrary.InstructionSchema;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        private void RotateLeft(ref byte reg, bool specialized = false)
        {
            var isCarrySet = TestFlag(InsSchema.FLAG_C);
            var isMSBSet = Helpers.TestBit(reg, 7);

            reg <<= 1;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_C, isMSBSet);

            if (isCarrySet)
            {
                Helpers.SetBit(ref reg, 0, true);
            }

            SetFlag(InsSchema.FLAG_Z, reg == 0 && !specialized);

            /*if (!specialized)
            {
                IncrementClock();
            }*/
        }

        private void RotateLeft(ushort address)
        {
            var value = ReadByte(address);
            RotateLeft(ref value);
            WriteByte(value, address);
        }

        private void RotateLeftNoCarry(ref byte reg, bool specialized = false)
        {
            var isMSBSet = Helpers.TestBit(reg, 7);

            reg <<= 1;

            _registers.F = 0;

            if (isMSBSet)
            {
                SetFlag(InsSchema.FLAG_C);
                Helpers.SetBit(ref reg, 0, true);
            }

            SetFlag(InsSchema.FLAG_Z, reg == 0 && !specialized);

            /*if (!specialized)
            {
                IncrementClock();
            }*/
        }

        private void LogicalShiftLeft(ref byte reg)
        {
            var isMSBSet = Helpers.TestBit(reg, 7);
            reg <<= 1;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, reg == 0);
            SetFlag(InsSchema.FLAG_C, isMSBSet);
        }

        private void LogicalShiftLeft(ushort address)
        {
            var value = ReadByte(address);
            LogicalShiftLeft(ref value);
            WriteByte(value, address);
        }

        private void RotateLeftNoCarry(ushort address)
        {
            var value = ReadByte(address);
            RotateLeftNoCarry(ref value);
            WriteByte(value, address);
        }

        private void RotateRight(ref byte reg, bool specialized = false)
        {
            var isCarrySet = TestFlag(InsSchema.FLAG_C);
            var isLSBSet = Helpers.TestBit(reg, 0);

            reg >>= 1;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_C, isLSBSet);

            if (isCarrySet)
            {
                Helpers.SetBit(ref reg, 7, true);
            }

            SetFlag(InsSchema.FLAG_Z, reg == 0 && !specialized);

            /*if (!specialized)
            {
                IncrementClock();
            }*/
        }

        private void RotateRight(ushort address)
        {
            var value = ReadByte(address);
            RotateRight(ref value);
            WriteByte(value, address);
        }

        private void RotateRightNoCarry(ref byte reg, bool specialized = false)
        {
            var isLSBSet = Helpers.TestBit(reg, 0);

            reg >>= 1;

            _registers.F = 0;

            if (isLSBSet)
            {
                SetFlag(InsSchema.FLAG_C);
                Helpers.SetBit(ref reg, 7, true);
            }

            SetFlag(InsSchema.FLAG_Z, reg == 0 && !specialized);

            /*if (!specialized)
            {
                IncrementClock();
            }*/
        }

        private void RotateRightNoCarry(ushort address)
        {
            var value = ReadByte(address);
            RotateRightNoCarry(ref value);
            WriteByte(value, address);
        }

        private void LogicalShiftRight(ref byte reg)
        {
            var isLSBSet = Helpers.TestBit(reg, 0);

            reg >>= 1;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_C, isLSBSet);
            SetFlag(InsSchema.FLAG_Z, reg == 0);
        }

        private void LogicalShiftRight(ushort address)
        {
            var value  = ReadByte(address);
            LogicalShiftRight(ref value);
            WriteByte(value, address);
        }

        private void ArithmeticShiftRight(ref byte reg)
        {
            var isMSBSet = Helpers.TestBit(reg, 7);
            var isLSBSet = Helpers.TestBit(reg, 0);

            reg >>= 1;

            Helpers.SetBit(ref reg, 7, isMSBSet);

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_C, isLSBSet);
            SetFlag(InsSchema.FLAG_Z, reg == 0);
        }

        private void ArithmeticShiftRight(ushort address)
        {
            var value = ReadByte(address);
            ArithmeticShiftRight(ref value);
            WriteByte(value, address);
        }

        private void Add(byte data)
        {
            var orig = _registers.A;
            int result = orig + data;

            _registers.A = (byte)result;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, _registers.A == 0);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) + (data & 0xF) > 0xF);
            SetFlag(InsSchema.FLAG_C, result > 0xFF);
        }

        private void Add(ushort data)
        {
            int result = _registers.HL + data;

            SetFlag(InsSchema.FLAG_N, false);

            SetFlag(InsSchema.FLAG_C, (result & 0x10000) != 0);
            SetFlag(InsSchema.FLAG_H, ((_registers.HL ^ data ^ (result & 0xFFFF)) & 0x1000) != 0);

            _registers.HL = (ushort)result;

            IncrementClock();
        }

        private void AddCarry(byte data)
        {
            var orig = _registers.A;

            var carryAddition = TestFlag(InsSchema.FLAG_C) ? 1 : 0;
            int result = orig + data + carryAddition;
            _registers.A = (byte)result;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, _registers.A == 0);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) + (data & 0xF) + carryAddition > 0xF);
            SetFlag(InsSchema.FLAG_C, result > 0xFF);
        }

        private void AddImmediateToSP()
        {
            IncrementClock(2);

            var val = ReadByte(_pc++);

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_C, (_sp.Lo + val) > 0xFF);
            SetFlag(InsSchema.FLAG_H, ((_sp.P & 0x0F) + (val & 0x0f)) > 0x0F);

            _sp.P = (ushort)(_sp.P + (sbyte)val);
        }

        // TODO either refactor this or the AddCarry function
        private void Subtract(byte data, bool subtractCarryFlag = false)
        {
            var orig = _registers.A;
            var carrySub = subtractCarryFlag && TestFlag(InsSchema.FLAG_C) ? 1 : 0;
            var result = orig - data - carrySub;

            _registers.A = (byte)result;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, _registers.A == 0);
            SetFlag(InsSchema.FLAG_N);
            SetFlag(InsSchema.FLAG_C, result < 0);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) - (data & 0xF) - carrySub < 0);
        }

        private void Increment(ref byte reg)
        {
            var orig = reg;

            reg++;

            SetFlag(InsSchema.FLAG_Z, reg == 0);
            SetFlag(InsSchema.FLAG_N, false);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) == 0xF);
        }

        private void IncrementMemory(int address)
        {
            var orig = ReadByte(address);
            var val = (byte)(orig + 1);

            WriteByte(val, address);

            SetFlag(InsSchema.FLAG_Z, val == 0);
            SetFlag(InsSchema.FLAG_N, false);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) == 0xF);
        }

        private void Decrement(ref byte reg)
        {
            var orig = reg;

            reg--;

            SetFlag(InsSchema.FLAG_Z, reg == 0);
            SetFlag(InsSchema.FLAG_N);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) == 0);
        }

        private void DecrementMemory(int address)
        {
            var orig = ReadByte(address);
            var val = (byte)(orig - 1);

            WriteByte(val, address);

            SetFlag(InsSchema.FLAG_Z, val == 0);
            SetFlag(InsSchema.FLAG_N);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) == 0);
        }
    }
}