using InsSchema = GBZEmuLibrary.InstructionSchema;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        private void XOR(byte xor)
        {
            _registers.A ^= xor;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, _registers.A == 0);
        }

        private void OR(byte or)
        {
            _registers.A |= or;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, _registers.A == 0);
        }

        private void AND(byte and)
        {
            _registers.A &= and;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, _registers.A == 0);

            SetFlag(InsSchema.FLAG_H);
        }

        private void Compliment()
        {
            _registers.A ^= 0xFF;

            SetFlag(InsSchema.FLAG_N);
            SetFlag(InsSchema.FLAG_H);
        }

        private void BitTest(int bit, byte reg)
        {
            SetFlag(InsSchema.FLAG_Z, !Helpers.TestBit(reg, bit));
            SetFlag(InsSchema.FLAG_N, false);
            SetFlag(InsSchema.FLAG_H);
        }

        private void BitTest(int bit, ushort address)
        {
            var value  = ReadByte(address);
            BitTest(bit, value);
        }

        private void ClearBit(int bit, ref byte reg)
        {
            Helpers.SetBit(ref reg, bit, false);
        }

        private void ClearBit(int bit, ushort address)
        {
            var value  = ReadByte(address);
            ClearBit(bit, ref value);
            WriteByte(value, address);
        }

        private void SetBit(int bit, ref byte reg)
        {
            Helpers.SetBit(ref reg, bit, true);
        }

        private void SetBit(int bit, ushort address)
        {
            var value  = ReadByte(address);
            SetBit(bit, ref value);
            WriteByte(value, address);
        }

        private void Compare(byte value)
        {
            var a = _registers.A;
            var orig = _registers.A;
            a -= value;

            _registers.F = 0;

            SetFlag(InsSchema.FLAG_Z, a == 0);
            SetFlag(InsSchema.FLAG_N);
            SetFlag(InsSchema.FLAG_C, orig < value);
            SetFlag(InsSchema.FLAG_H, (orig & 0xF) - (value & 0xF) < 0);
        }

        private void Stop()
        {
            _stopped = true;

            if (_pendingSpeedSwitch)
            {
                _pendingSpeedSwitch = false;
                _doubleSpeed = !_doubleSpeed;
            }
        }

        //TODO double check
        private void DAA()
        {
            int a = _registers.A;

            if (TestFlag(InsSchema.FLAG_N))
            {
                if (TestFlag(InsSchema.FLAG_H))
                {
                    a -= 0x06;
                }

                if (TestFlag(InsSchema.FLAG_C))
                {
                    a -= 0x60;
                }
            }
            else
            {
                if ((a & 0x0F) > 0x09 || TestFlag(InsSchema.FLAG_H))
                {
                    a += 0x06;
                }

                if ((a >> 4) > 0x09 || TestFlag(InsSchema.FLAG_C))
                {
                    a += 0x60;
                }
            }

            SetFlag(InsSchema.FLAG_H, false);
            SetFlag(InsSchema.FLAG_Z, false);

            if (a > 0xFF)
            {
                SetFlag(InsSchema.FLAG_C);
            }

            _registers.A = (byte)(a & 0xFF);

            if (_registers.A == 0)
            {
                SetFlag(InsSchema.FLAG_Z);
            }
        }

        private void Swap(ref byte reg)
        {
            reg = (byte)(((reg & 0xF0) >> 4) | ((reg & 0x0F) << 4));

            _registers.F = 0;
            SetFlag(InsSchema.FLAG_Z, reg == 0);
        }

        private void Swap(ushort address)
        {
            var value  = ReadByte(address);
            Swap(ref value);
            WriteByte(value, address);
        }
    }
}