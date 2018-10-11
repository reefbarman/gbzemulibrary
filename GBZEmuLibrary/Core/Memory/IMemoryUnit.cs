namespace GBZEmuLibrary
{
    internal interface IMemoryUnit
    {
        bool CanReadWriteByte(int address);

        byte ReadByte(int address);
        void WriteByte(byte data, int address);
    }
}