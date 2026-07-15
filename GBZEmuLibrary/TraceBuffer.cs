using System;

namespace GBZEmuLibrary
{
    public sealed class TraceBuffer
    {
        private readonly string[] _entries;
        private int _start;
        private int _count;

        public bool Enabled { get; set; }
        public ulong StartInstruction { get; set; }
        public ulong? StopInstruction { get; set; }
        public ushort? BreakProgramCounter { get; set; }
        public int Capacity => _entries.Length;

        internal ushort[] BreakProgramCounters { get; set; }
        public int Count => _count;

        internal TraceBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _entries = new string[capacity];
        }

        public string[] GetEntries()
        {
            var result = new string[_count];

            for (var i = 0; i < _count; i++)
            {
                result[i] = _entries[(_start + i) % Capacity];
            }

            return result;
        }

        public void Clear()
        {
            Array.Clear(_entries, 0, _entries.Length);
            _start = 0;
            _count = 0;
        }

        internal bool ShouldCapture(ulong instructionCount)
        {
            return Enabled &&
                   instructionCount >= StartInstruction &&
                   (!StopInstruction.HasValue || instructionCount <= StopInstruction.Value);
        }

        internal bool ShouldBreak(ushort programCounter)
        {
            if (BreakProgramCounter.HasValue && BreakProgramCounter.Value == programCounter)
            {
                return true;
            }

            if (BreakProgramCounters == null)
            {
                return false;
            }

            for (var i = 0; i < BreakProgramCounters.Length; i++)
            {
                if (BreakProgramCounters[i] == programCounter)
                {
                    return true;
                }
            }

            return false;
        }

        internal void Add(string entry)
        {
            if (_count < Capacity)
            {
                _entries[(_start + _count) % Capacity] = entry;
                _count++;
                return;
            }

            _entries[_start] = entry;
            _start = (_start + 1) % Capacity;
        }
    }
}
