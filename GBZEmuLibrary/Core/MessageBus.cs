using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Connects the hardware components belonging to one emulator instance without process-global callback state.
    /// </summary>
    internal sealed class MessageBus
    {
        public Action<Interrupts> OnRequestInterrupt;

        public Func<int, byte> OnReadCgbDmaSourceByte;
        public Action<byte, int> OnWriteCgbDmaDestinationByte;
        public Func<int, byte> OnReadOamDmaSourceByte;
        public Action<byte, int> OnWriteOamDmaByte;

        public Action OnHBlank;
        public Action OnHBlankDmaWindow;
        public Action OnVBlank;
        public Func<bool> OnCanStartHBlankDmaImmediately;
        public Func<bool> OnIsCpuHalted;
        public Func<int> OnGetCpuSpeedFactor;
        [SaveStateIgnore]
        private ITimingObserver _timingObserver;

        /// <summary>
        /// Installs an optional internal observer for instance bus callback boundaries.
        /// </summary>
        internal void SetTimingObserver(ITimingObserver timingObserver)
        {
            _timingObserver = timingObserver;
        }

        /// <summary>
        /// Emits one allocation-free timing event through this emulator instance.
        /// </summary>
        internal void ObserveTiming(in TimingEvent timingEvent)
        {
            _timingObserver?.Observe(in timingEvent);
        }

        /// <summary>
        /// Routes an interrupt request to this emulator's CPU interrupt handler.
        /// </summary>
        public void RequestInterrupt(Interrupts interrupt)
        {
            OnRequestInterrupt?.Invoke(interrupt);
        }

        /// <summary>
        /// Reads a CGB DMA source through its privileged mapped-memory port.
        /// </summary>
        public byte ReadCgbDmaSourceByte(int address)
        {
            return (byte)OnReadCgbDmaSourceByte?.Invoke(address);
        }

        /// <summary>
        /// Writes a CGB DMA byte through its privileged mapped-memory destination port.
        /// </summary>
        public void WriteCgbDmaDestinationByte(byte data, int address)
        {
            OnWriteCgbDmaDestinationByte?.Invoke(data, address);
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
            var timingEvent = new TimingEvent(TimingEventKind.HBlankStarted);
            ObserveTiming(in timingEvent);
            OnHBlank?.Invoke();
        }

        /// <summary>
        /// Notifies CGB VRAM DMA that the measured pre-HBlank bus-acquisition window has opened.
        /// </summary>
        public void HBlankDmaWindowOpened()
        {
            var timingEvent = new TimingEvent(TimingEventKind.HBlankDmaWindowOpened);
            ObserveTiming(in timingEvent);
            OnHBlankDmaWindow?.Invoke();
        }

        /// <summary>
        /// Reports whether an HBlank DMA start occurs with the LCD disabled or during an active HBlank.
        /// </summary>
        public bool CanStartHBlankDmaImmediately()
        {
            return OnCanStartHBlankDmaImmediately?.Invoke() == true;
        }

        /// <summary>
        /// Reports whether HALT currently prevents an HBlank DMA block from starting.
        /// </summary>
        public bool IsCpuHalted()
        {
            return OnIsCpuHalted?.Invoke() == true;
        }

        /// <summary>
        /// Returns the number of raw CPU clock periods per base-speed CGB clock period.
        /// </summary>
        public int GetCpuSpeedFactor()
        {
            return OnGetCpuSpeedFactor?.Invoke() ?? 1;
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
