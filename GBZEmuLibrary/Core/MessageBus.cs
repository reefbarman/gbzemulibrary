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
        public Func<int, byte> OnReadOamDmaSourceByte;
        public Action<byte, int> OnWriteOamDmaByte;

        public Action OnHBlank;
        public Action OnVBlank;
        public Func<bool> OnCanStartHBlankDmaImmediately;

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
        /// Reads an OAM DMA source without applying CPU or PPU bus restrictions.
        /// </summary>
        public byte ReadOamDmaSourceByte(int address)
        {
            return (byte)OnReadOamDmaSourceByte?.Invoke(address);
        }

        /// <summary>
        /// Writes one OAM DMA byte through the PPU's DMA-owned OAM port.
        /// </summary>
        public void WriteOamDmaByte(byte data, int address)
        {
            OnWriteOamDmaByte?.Invoke(data, address);
        }

        /// <summary>
        /// Notifies this emulator's DMA controller that its PPU entered HBlank.
        /// </summary>
        public void HBlankStarted()
        {
            OnHBlank?.Invoke();
        }

        /// <summary>
        /// Reports whether an HBlank DMA start occurs with the LCD disabled or during an active HBlank.
        /// </summary>
        public bool CanStartHBlankDmaImmediately()
        {
            return OnCanStartHBlankDmaImmediately?.Invoke() == true;
        }

        /// <summary>
        /// Notifies instance-owned peripherals at the VBlank interrupt request boundary.
        /// </summary>
        public void VBlankStarted()
        {
            OnVBlank?.Invoke();
        }
    }
}
