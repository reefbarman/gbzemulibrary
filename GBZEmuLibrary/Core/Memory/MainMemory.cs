using GBZEmuLibrary;

public class MainMemory : IMemoryUnit
{
    private readonly byte[] _memory = new byte[MemorySchema.MAX_RAM_SIZE];

    public bool InBootROM { get; set; } = true;

    public bool CanReadWriteByte(int address)
    {
        throw new System.NotImplementedException();
    }

    public byte ReadByte(int address)
    {
        return _memory[address];
    }

    public void WriteByte(byte data, int address)
    {
        _memory[address] = data;

        if (address == MemorySchema.BOOT_ROM_DISABLE_REGISTER)
        {
            InBootROM = false;
        }
    }
}
