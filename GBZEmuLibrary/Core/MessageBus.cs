using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Connects the hardware components belonging to one emulator instance without process-global callback state.
    /// </summary>
    internal sealed class MessageBus
    {
        public Action<Interrupts> OnRequestInterrupt;

        public Func<int, byte> OnReadByte;
        public Action<byte, int> OnWriteByte;

        public Action OnHBlank;

        /// <summary>
        /// Routes an interrupt request to this emulator's CPU interrupt handler.
        /// </summary>
        public void RequestInterrupt(Interrupts interrupt)
        {
            OnRequestInterrupt?.Invoke(interrupt);
        }

        /// <summary>
        /// Reads through this emulator's MMU callback for DMA transfers.
        /// </summary>
        public byte ReadByte(int address)
        {
            return (byte)OnReadByte?.Invoke(address);
        }

        /// <summary>
        /// Writes through this emulator's MMU callback for DMA transfers.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            OnWriteByte?.Invoke(data, address);
        }

        /// <summary>
        /// Notifies this emulator's DMA controller that its PPU entered HBlank.
        /// </summary>
        public void HBlankStarted()
        {
            OnHBlank?.Invoke();
        }
    }
}
