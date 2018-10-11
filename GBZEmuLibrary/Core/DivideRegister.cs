namespace GBZEmuLibrary
{
    internal class DivideRegister : IMemoryUnit
    {
        private const int CYCLES_PER_DIVIDER_UPDATE = GameBoySchema.MAX_DMG_CLOCK_CYCLES / 16384;

        private byte _register; //TODO does this get initialized to a special value?
        private int _counter;

        public void Update(int cycles)
        {
            _counter += cycles;

            while (_counter >= CYCLES_PER_DIVIDER_UPDATE)
            {
                _counter -= CYCLES_PER_DIVIDER_UPDATE;
                _register++;
            }
        }

        public void WriteByte(byte data, int address)
        {
            _register = 0;
        }

        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.DIVIDE_REGISTER;
        }

        public byte ReadByte(int address)
        {
            return _register;
        }
    }
}
