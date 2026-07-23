namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies the CPU machine-cycle role carried by a structural bus transaction.
    /// </summary>
    internal enum CpuMachineCycleKind
    {
        Internal,
        OpcodeFetch,
        MemoryRead,
        MemoryWrite,
        Halt,
        DmaStall
    }

    /// <summary>
    /// Carries fixed-size CPU bus metadata and cycle-start OAM-DMA ownership through transaction completion.
    /// </summary>
    internal readonly struct CpuBusTransaction
    {
        internal CpuBusTransaction(
            CpuMachineCycleKind kind,
            ushort address,
            byte writeData,
            bool oamDmaBlockedAtT1,
            byte oamDmaBusValueAtT1,
            bool readDataLatchedBeforeCompletion = false,
            byte readDataBeforeCompletion = 0,
            bool writeDataLatchedBeforeCompletion = false)
        {
            Kind = kind;
            Address = address;
            WriteData = writeData;
            OamDmaBlockedAtT1 = oamDmaBlockedAtT1;
            OamDmaBusValueAtT1 = oamDmaBusValueAtT1;
            ReadDataLatchedBeforeCompletion = readDataLatchedBeforeCompletion;
            ReadDataBeforeCompletion = readDataBeforeCompletion;
            WriteDataLatchedBeforeCompletion = writeDataLatchedBeforeCompletion;
        }

        internal CpuMachineCycleKind Kind { get; }
        internal ushort Address { get; }
        internal byte WriteData { get; }
        internal bool OamDmaBlockedAtT1 { get; }
        internal byte OamDmaBusValueAtT1 { get; }
        internal bool ReadDataLatchedBeforeCompletion { get; }
        internal byte ReadDataBeforeCompletion { get; }
        internal bool WriteDataLatchedBeforeCompletion { get; }
    }
}
