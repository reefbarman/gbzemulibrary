using System;

namespace GBZEmuLibrary
{
    internal sealed class SerialRegisters : IMemoryUnit
    {
        public event Action<byte> ByteTransferred;

        private byte _data;
        private byte _control;

        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.SERIAL_DATA_REGISTER ||
                   address == MemorySchema.SERIAL_CONTROL_REGISTER;
        }

        public byte ReadByte(int address)
        {
            switch (address)
            {
                case MemorySchema.SERIAL_DATA_REGISTER:
                    return _data;
                case MemorySchema.SERIAL_CONTROL_REGISTER:
                    return _control;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        public void WriteByte(byte data, int address)
        {
            switch (address)
            {
                case MemorySchema.SERIAL_DATA_REGISTER:
                    _data = data;
                    return;
                case MemorySchema.SERIAL_CONTROL_REGISTER:
                    _control = data;

                    if (Helpers.TestBit(data, 7) && Helpers.TestBit(data, 0))
                    {
                        _control &= 0x7F;
                        ByteTransferred?.Invoke(_data);
                    }

                    return;
                default:
                    throw new IndexOutOfRangeException();
            }
        }
    }
}
