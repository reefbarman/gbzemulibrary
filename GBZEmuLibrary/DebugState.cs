namespace GBZEmuLibrary
{
    public readonly struct CpuDebugState
    {
        public ushort PC { get; }
        public ushort SP { get; }
        public ushort AF { get; }
        public ushort BC { get; }
        public ushort DE { get; }
        public ushort HL { get; }
        public bool ZeroFlag { get; }
        public bool SubtractFlag { get; }
        public bool HalfCarryFlag { get; }
        public bool CarryFlag { get; }
        public bool InterruptsEnabled { get; }
        public bool InterruptEnablePending { get; }
        public bool InterruptDisablePending { get; }
        public bool Halted { get; }
        public bool DoubleSpeed { get; }
        public ulong TotalClockCycles { get; }
        public ulong ExecutedInstructionCount { get; }

        internal CpuDebugState(
            ushort pc,
            ushort sp,
            ushort af,
            ushort bc,
            ushort de,
            ushort hl,
            bool interruptsEnabled,
            bool interruptEnablePending,
            bool interruptDisablePending,
            bool halted,
            bool doubleSpeed,
            ulong totalClockCycles,
            ulong executedInstructionCount)
        {
            PC = pc;
            SP = sp;
            AF = af;
            BC = bc;
            DE = de;
            HL = hl;
            ZeroFlag = Helpers.TestBit((byte)af, InstructionSchema.FLAG_Z);
            SubtractFlag = Helpers.TestBit((byte)af, InstructionSchema.FLAG_N);
            HalfCarryFlag = Helpers.TestBit((byte)af, InstructionSchema.FLAG_H);
            CarryFlag = Helpers.TestBit((byte)af, InstructionSchema.FLAG_C);
            InterruptsEnabled = interruptsEnabled;
            InterruptEnablePending = interruptEnablePending;
            InterruptDisablePending = interruptDisablePending;
            Halted = halted;
            DoubleSpeed = doubleSpeed;
            TotalClockCycles = totalClockCycles;
            ExecutedInstructionCount = executedInstructionCount;
        }
    }

    public readonly struct PpuDebugState
    {
        public byte ScanLine { get; }
        public byte LcdControl { get; }
        public byte LcdStatus { get; }
        public int Mode { get; }
        public int ModeClockCycles { get; }

        internal PpuDebugState(byte scanLine, byte lcdControl, byte lcdStatus, int modeClockCycles)
        {
            ScanLine = scanLine;
            LcdControl = lcdControl;
            LcdStatus = lcdStatus;
            Mode = lcdStatus & 0x03;
            ModeClockCycles = modeClockCycles;
        }
    }
}
