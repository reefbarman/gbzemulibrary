using System;

namespace GBZEmuLibrary
{
    public sealed class EmulatorDebugger
    {
        private readonly CPU _cpu;
        private readonly MMU _mmu;
        private readonly GPU _gpu;
        private readonly SerialRegisters _serialRegisters;
        private readonly Func<bool> _isRunning;
        private readonly Action _update;
        private bool _stopRequested;

        internal event Action LoadBBExecuted;

        public TraceBuffer Trace { get; }
        public bool IsStopped => _stopRequested;

        internal bool StopRequested => _stopRequested;

        internal EmulatorDebugger(CPU cpu, MMU mmu, GPU gpu, SerialRegisters serialRegisters, Func<bool> isRunning, Action update)
        {
            _cpu = cpu;
            _mmu = mmu;
            _gpu = gpu;
            _serialRegisters = serialRegisters;
            _isRunning = isRunning;
            _update = update;
            Trace = new TraceBuffer(4096);
            _cpu.SetTraceBuffer(Trace);
            _cpu.LoadBBExecuted = () => LoadBBExecuted?.Invoke();
            _cpu.BreakpointHit = RequestStop;
        }

        public event Action<byte> SerialByteTransferred
        {
            add { _serialRegisters.ByteTransferred += value; }
            remove { _serialRegisters.ByteTransferred -= value; }
        }

        public CpuDebugState GetCpuState()
        {
            EnsureRunning();
            return _cpu.GetDebugState();
        }

        public PpuDebugState GetPpuState()
        {
            EnsureRunning();
            return _gpu.GetDebugState();
        }

        public byte PeekByte(int address)
        {
            EnsureRunning();
            ValidateAddress(address);
            return _mmu.ReadByteUntimed(address);
        }

        public void PokeByte(byte value, int address)
        {
            EnsureRunning();
            ValidateAddress(address);
            _mmu.WriteByteUntimed(value, address);
        }

        public void RequestStop()
        {
            EnsureRunning();
            _stopRequested = true;
        }

        public void Resume()
        {
            EnsureRunning();
            _stopRequested = false;
        }

        /// <summary>
        /// Runs complete emulator frames until execution reaches the requested pre-fetch program counter.
        /// The emulator remains stopped at the breakpoint when this returns true and remains running on timeout.
        /// </summary>
        /// <param name="programCounter">The 16-bit instruction address at which execution should stop.</param>
        /// <param name="maxFrames">The maximum number of hardware frames to execute.</param>
        /// <returns>True when the breakpoint was reached; otherwise false after the frame budget was exhausted.</returns>
        public bool RunUntilProgramCounter(ushort programCounter, int maxFrames)
        {
            EnsureRunning();

            if (maxFrames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrames));
            }

            Trace.BreakProgramCounters = null;
            Trace.BreakProgramCounter = programCounter;
            Resume();

            for (var frame = 0; frame < maxFrames && !_stopRequested; frame++)
            {
                _update();
            }

            return _stopRequested;
        }

        /// <summary>
        /// Runs complete emulator frames until execution reaches any requested pre-fetch program counter.
        /// </summary>
        /// <param name="programCounters">The instruction addresses that should stop execution.</param>
        /// <param name="maxFrames">The maximum number of hardware frames to execute.</param>
        /// <returns>The reached program counter, or null when the frame budget was exhausted.</returns>
        public ushort? RunUntilAnyProgramCounter(ushort[] programCounters, int maxFrames)
        {
            EnsureRunning();

            if (programCounters == null)
            {
                throw new ArgumentNullException(nameof(programCounters));
            }

            if (programCounters.Length == 0)
            {
                throw new ArgumentException("At least one program counter is required.", nameof(programCounters));
            }

            if (maxFrames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrames));
            }

            Trace.BreakProgramCounter = null;
            Trace.BreakProgramCounters = (ushort[])programCounters.Clone();
            Resume();

            for (var frame = 0; frame < maxFrames && !_stopRequested; frame++)
            {
                _update();
            }

            return _stopRequested ? _cpu.GetDebugState().PC : (ushort?)null;
        }

        private void EnsureRunning()
        {
            if (!_isRunning())
            {
                throw new InvalidOperationException("Debug state is only available while the emulator is running.");
            }
        }

        private static void ValidateAddress(int address)
        {
            if (address < 0 || address >= MemorySchema.MAX_RAM_SIZE)
            {
                throw new ArgumentOutOfRangeException(nameof(address));
            }
        }
    }
}
