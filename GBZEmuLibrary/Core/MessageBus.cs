using System;

namespace GBZEmuLibrary
{
    internal class MessageBus
    {
        private static MessageBus _instance;

        public static MessageBus Instance => _instance ?? (_instance = new MessageBus());

        public Action<Interrupts> OnRequestInterrupt;

        public Func<int, byte> OnReadByte;
        public Action<byte, int> OnWriteByte;

        public Action OnHBlank;

        public void RequestInterrupt(Interrupts interrupt)
        {
            OnRequestInterrupt?.Invoke(interrupt);
        }

        public byte ReadByte(int address)
        {
            return (byte)OnReadByte?.Invoke(address);
        }

        public void WriteByte(byte data, int address)
        {
            OnWriteByte?.Invoke(data, address);
        }

        public void HBlankStarted()
        {
            OnHBlank?.Invoke();
        }
    }
}
