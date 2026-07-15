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
        private bool _stopRequested;

        internal event Action LoadBBExecuted;

        public TraceBuffer Trace { get; }
        public bool IsStopped => _stopRequested;

        internal bool StopRequested => _stopRequested;

        internal EmulatorDebugger(CPU cpu, MMU mmu, GPU gpu, SerialRegisters serialRegisters, Func<bool> isRunning)
        {
            _cpu = cpu;
            _mmu = mmu;
            _gpu = gpu;
            _serialRegisters = serialRegisters;
            _isRunning = isRunning;
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
            return _mmu.ReadByte(address);
        }

        public void PokeByte(byte value, int address)
        {
            EnsureRunning();
            ValidateAddress(address);
            _mmu.WriteByte(value, address);
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
