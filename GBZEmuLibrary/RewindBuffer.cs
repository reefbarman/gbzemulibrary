using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Stores a bounded ring of emulator checkpoints and restores earlier checkpoints without host-framework dependencies.
    /// </summary>
    public sealed class RewindBuffer
    {
        private readonly EmulatorState[] _states;
        private int _start;
        private int _count;
        private Emulator _owner;

        /// <summary>
        /// Gets the maximum number of checkpoints retained.
        /// </summary>
        public int Capacity => _states.Length;

        /// <summary>
        /// Gets the number of checkpoints currently retained.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Creates a rewind history. The host controls duration by choosing capacity and capture cadence.
        /// </summary>
        public RewindBuffer(int capacity)
        {
            if (capacity < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Rewind capacity must retain at least two checkpoints.");
            }

            _states = new EmulatorState[capacity];
        }

        /// <summary>
        /// Captures the running emulator, dropping the oldest checkpoint when the buffer is full.
        /// </summary>
        public void Capture(Emulator emulator)
        {
            if (emulator == null)
            {
                throw new ArgumentNullException(nameof(emulator));
            }

            EnsureOwner(emulator);

            var state = emulator.CaptureState();
            if (_count == Capacity)
            {
                _states[_start] = state;
                _start = (_start + 1) % Capacity;
                return;
            }

            _states[PhysicalIndex(_count)] = state;
            _count++;
        }

        /// <summary>
        /// Discards the newest checkpoint and restores the preceding one, returning false at the oldest retained point.
        /// </summary>
        public bool TryRewind(Emulator emulator)
        {
            if (emulator == null)
            {
                throw new ArgumentNullException(nameof(emulator));
            }

            if (_count < 2)
            {
                return false;
            }

            EnsureOwner(emulator);

            var newestIndex = PhysicalIndex(_count - 1);
            emulator.RestoreState(_states[PhysicalIndex(_count - 2)]);
            _states[newestIndex] = null;
            _count--;
            return true;
        }

        /// <summary>
        /// Removes all retained checkpoints and releases their serialized storage.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_states, 0, _states.Length);
            _start = 0;
            _count = 0;
            _owner = null;
        }

        private int PhysicalIndex(int logicalIndex)
        {
            return (_start + logicalIndex) % Capacity;
        }

        private void EnsureOwner(Emulator emulator)
        {
            if (_owner == null)
            {
                _owner = emulator;
                return;
            }

            if (!ReferenceEquals(_owner, emulator))
            {
                throw new InvalidOperationException(
                    "A rewind buffer cannot mix checkpoints from different emulator instances. Clear it first.");
            }
        }
    }
}
