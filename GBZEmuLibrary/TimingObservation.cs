namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies a real boundary in the current CPU machine-cycle and subsystem-update implementation.
    /// </summary>
    internal enum TimingEventKind
    {
        CpuReadObserved,
        CpuWriteObserved,
        InterruptDispatchCycle,
        InterruptSelected,
        InterruptAcknowledged,
        HBlankDmaWindowOpened,
        HBlankStarted,
        GeneralPurposeDmaBlockCopied,
        HBlankDmaBlockCopied,
        MachineCycleStarted,
        SystemUpdateStarted,
        ApuFrameSequencerClocked,
        SerialBitShifted,
        TimerUpdateCompleted,
        SerialUpdateCompleted,
        DmaUpdateCompleted,
        CartridgeUpdateCompleted,
        GpuUpdateCompleted,
        ApuUpdateCompleted,
        SystemUpdateCompleted,
        MachineCycleCompleted
    }

    /// <summary>
    /// Identifies the five logical machine cycles in one interrupt dispatch sequence.
    /// </summary>
    internal enum InterruptDispatchCycle
    {
        First,
        Internal,
        HighStackWrite,
        LowStackWrite,
        Final
    }

    /// <summary>
    /// Describes one allocation-free timing observation emitted to focused internal tests.
    /// </summary>
    internal readonly struct TimingEvent
    {
        public TimingEventKind Kind { get; }
        public int Address { get; }
        public byte Value { get; }
        public int Clocks { get; }
        public bool BlockedByOamDma { get; }

        /// <summary>
        /// Creates an immutable timing event for the current implementation boundary.
        /// </summary>
        internal TimingEvent(
            TimingEventKind kind,
            int address = -1,
            byte value = 0,
            int clocks = 0,
            bool blockedByOamDma = false)
        {
            Kind = kind;
            Address = address;
            Value = value;
            Clocks = clocks;
            BlockedByOamDma = blockedByOamDma;
        }
    }

    /// <summary>
    /// Receives internal timing events without requiring production logging or event allocation.
    /// </summary>
    internal interface ITimingObserver
    {
        /// <summary>
        /// Observes one current CPU or subsystem timing boundary.
        /// </summary>
        void Observe(in TimingEvent timingEvent);
    }
}
