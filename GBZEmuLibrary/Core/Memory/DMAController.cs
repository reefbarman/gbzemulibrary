using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates OAM DMA and the CGB general-purpose/HBlank DMA engines that copy data into VRAM.
    /// </summary>
    internal class DMAController : IMemoryUnit
    {
        private const int CGB_DMA_BLOCK_SIZE = 0x10;
        private const int CGB_DMA_BLOCK_CLOCKS = 32;
        private const int CGB_DMA_BLOCK_OVERHEAD_CLOCKS = 4;
        private const int OAM_DMA_LENGTH = 0xA0;
        private const int OAM_DMA_START_DELAY_M_CYCLES = 2;

        private byte _sourceHigh;
        private byte _sourceLow;

        private byte _destinationHigh;
        private byte _destinationLow;

        private byte _dmaLengthMode;

        private byte _oamDmaSourceHigh;
        private byte _activeOamDmaSourceHigh;
        private int _oamDmaIndex;
        private int _oamDmaStartDelay;
        private bool _oamDmaActive;
        private byte _oamDmaBusValue;
        private GBCMode _mode;

        private bool _transferring;
        private int _hblankDmaClocksRemaining;
        private int _rawClockInMachineCycle;

        private readonly MessageBus _messageBus;

        /// <summary>
        /// Creates a DMA controller connected to the memory and HBlank bus for its owning emulator.
        /// </summary>
        public DMAController(MessageBus messageBus)
        {
            _messageBus = messageBus;
            _messageBus.OnHBlankDmaWindow = OnHBlankDmaWindow;
        }

        /// <summary>
        /// Selects the OAM DMA address decoder and bus layout for the active hardware mode.
        /// </summary>
        public void Init(GBCMode mode)
        {
            _mode = mode;
        }

        /// <summary>
        /// Updates an OAM or CGB VRAM DMA register and starts or stops a transfer when its control register is written.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            switch (address)
            {
                case MemorySchema.DMA_REGISTER:
                    StartOamDma(data, includeCurrentCpuCycle: true);
                    break;

                case MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER:
                    _sourceHigh = data;
                    break;

                case MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER:
                    _sourceLow = data;
                    break;

                case MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER:
                    _destinationHigh = data;
                    break;

                case MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER:
                    _destinationLow = data;
                    break;

                case MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER:
                    WriteDmaControl(data, writeClocksRemaining: InstructionSchema.FOUR_CYCLES);
                    break;

            }
        }

        public bool CanReadWriteByte(int address)
        {
            if (address == MemorySchema.DMA_REGISTER)
            {
                return true;
            }

            if (address >= MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER && address <= MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether CPU read data depends on DMA state at the transaction-completion boundary.
        /// </summary>
        internal bool ReadsCpuDataAtCompletion(int address)
        {
            return address == MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER;
        }


        public byte ReadByte(int address)
        {
            switch (address)
            {
                case MemorySchema.DMA_REGISTER:
                    return _oamDmaSourceHigh;

                case MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER:
                case MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER:
                case MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER:
                case MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER:
                    return 0xFF;

                case MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER:
                    return _dmaLengthMode;
            }

            throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Applies a CPU write after its T4 clocks have elapsed.
        /// </summary>
        internal void WriteByteForCpuCompletion(byte data, int address)
        {
            if (address == MemorySchema.DMA_REGISTER)
            {
                StartOamDma(data, includeCurrentCpuCycle: false);
                return;
            }

            if (address == MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER)
            {
                WriteDmaControl(data, writeClocksRemaining: 1);
                return;
            }

            WriteByte(data, address);
        }

        /// <summary>
        /// Gets whether OAM DMA currently owns its source bus and OAM destination port.
        /// </summary>
        public bool IsOamDmaActive => _oamDmaActive;

        /// <summary>
        /// Gets the source high byte owned by the transfer currently driving the bus.
        /// </summary>
        public byte ActiveOamDmaSourceHigh => _activeOamDmaSourceHigh;

        /// <summary>
        /// Gets the source byte currently driven on the bus by active OAM DMA.
        /// </summary>
        public byte OamDmaBusValue => _oamDmaBusValue;

        /// <summary>
        /// Gets whether a scheduled HBlank DMA block currently prevents the CPU from executing instructions.
        /// </summary>
        public bool IsCpuStalledByHBlankDma => _hblankDmaClocksRemaining > 0;

        /// <summary>
        /// Advances DMA by one raw CPU clock. HDMA consumes every clock while OAM DMA steps only at T1.
        /// </summary>
        internal void AdvanceRawClock()
        {
            // Emulator advances this once per coordinator clock. Both phase counters start at zero and are restored
            // together, so this free-running counter remains aligned with CPU T1.
            _rawClockInMachineCycle++;
            var startsMachineCycle = _rawClockInMachineCycle == 1;
            if (_rawClockInMachineCycle == InstructionSchema.FOUR_CYCLES)
            {
                _rawClockInMachineCycle = 0;
            }

            if (_hblankDmaClocksRemaining > 0 && --_hblankDmaClocksRemaining == 0)
            {
                CopyActiveHBlankBlock();
            }

            if (!startsMachineCycle)
            {
                return;
            }

            AdvanceOamDmaMachineCycle();
        }

        /// <summary>
        /// Advances complete raw clocks while preserving the historical component-test API.
        /// </summary>
        public void Update(int cycles)
        {
            for (var elapsed = 0; elapsed < cycles; elapsed++)
            {
                AdvanceRawClock();
            }
        }

        /// <summary>
        /// Advances the OAM DMA startup pipeline or copies one active byte for one CPU machine cycle.
        /// </summary>
        private void AdvanceOamDmaMachineCycle()
        {
            if (_oamDmaStartDelay > 0)
            {
                if (_oamDmaActive)
                {
                    CopyOamDmaByte();
                }

                _oamDmaStartDelay--;
                if (_oamDmaStartDelay == 0)
                {
                    _activeOamDmaSourceHigh = _oamDmaSourceHigh;
                    _oamDmaIndex = 0;
                    _oamDmaActive = true;
                    LatchOamDmaBusValue();
                }

                return;
            }

            if (_oamDmaActive)
            {
                CopyOamDmaByte();
            }
        }

        /// <summary>
        /// Latches a new source and schedules it to take ownership after the two-cycle startup pipeline.
        /// An older transfer continues during that delay.
        /// </summary>
        private void StartOamDma(byte data, bool includeCurrentCpuCycle)
        {
            _oamDmaSourceHigh = data;
            _oamDmaStartDelay = OAM_DMA_START_DELAY_M_CYCLES - (includeCurrentCpuCycle ? 0 : 1);
        }

        /// <summary>
        /// Copies the current OAM DMA byte and releases the bus after all 160 bytes complete.
        /// </summary>
        private void CopyOamDmaByte()
        {
            _messageBus.WriteOamDmaByte(
                _oamDmaBusValue,
                MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + _oamDmaIndex);

            _oamDmaIndex++;
            if (_oamDmaIndex == OAM_DMA_LENGTH)
            {
                _oamDmaActive = false;
                return;
            }

            LatchOamDmaBusValue();
        }

        /// <summary>
        /// Reads the next source byte before its machine cycle so contending CPU accesses observe the DMA bus.
        /// </summary>
        private void LatchOamDmaBusValue()
        {
            var sourceAddress = (_activeOamDmaSourceHigh << 8) | _oamDmaIndex;
            if (sourceAddress >= MemorySchema.ECHO_RAM_START)
            {
                sourceAddress = _mode == GBCMode.NoGBC
                    ? sourceAddress - MemorySchema.WORK_RAM_ECHO_OFFSET
                    : MemorySchema.EXTERNAL_RAM_START | (sourceAddress & 0x1FFF);
            }

            _oamDmaBusValue = _messageBus.ReadOamDmaSourceByte(sourceAddress);
        }

        /// <summary>
        /// Starts or cancels CGB DMA at the caller's write-visibility boundary.
        /// </summary>
        private void WriteDmaControl(byte data, int writeClocksRemaining)
        {
            if (_transferring && !Helpers.TestBit(data, 7))
            {
                StopTransfer(data);
                return;
            }

            _dmaLengthMode = data;
            StartTransfer(writeClocksRemaining);
        }

        /// <summary>
        /// Cancels an active HBlank transfer and exposes the cancellation write through inactive HDMA5 readback.
        /// </summary>
        private void StopTransfer(byte data)
        {
            // A cancellation write reloads the visible remaining-length bits before marking HDMA5 inactive.
            _dmaLengthMode = (byte)(0x80 | (data & 0x7F));
            _transferring = false;
            _hblankDmaClocksRemaining = 0;
        }

        /// <summary>
        /// Starts clocked HDMA or completes the currently immediate GDMA implementation.
        /// </summary>
        private void StartTransfer(int writeClocksRemaining)
        {
            var hBlankMode = Helpers.TestBit(_dmaLengthMode, 7);
            _dmaLengthMode &= 0x7F;

            if (hBlankMode)
            {
                _transferring = true;
                if (_messageBus.CanStartHBlankDmaImmediately())
                {
                    ScheduleHBlankBlock(writeClocksRemaining);
                }

                return;
            }

            var blockCount = _dmaLengthMode + 1;
            for (var block = 0; block < blockCount; block++)
            {
                CopyBlock();
                var timingEvent = new TimingEvent(TimingEventKind.GeneralPurposeDmaBlockCopied);
                _messageBus.ObserveTiming(in timingEvent);
            }

            _dmaLengthMode = 0xFF;
        }

        /// <summary>
        /// Returns the 16-byte-aligned CGB DMA source address encoded by HDMA1 and HDMA2.
        /// </summary>
        private int GetSourceAddress()
        {
            return (_sourceHigh << 8) | (_sourceLow & 0xF0);
        }

        /// <summary>
        /// Returns the 16-byte-aligned VRAM destination address encoded by HDMA3 and HDMA4.
        /// </summary>
        private int GetDestinationAddress()
        {
            return MemorySchema.VIDEO_RAM_START | (((_destinationHigh & 0x1F) << 8) | _destinationLow & 0xF0);
        }

        /// <summary>
        /// Copies one 16-byte CGB DMA block and advances the hardware-owned source and destination addresses.
        /// </summary>
        private void CopyBlock()
        {
            var sourceAddress = GetSourceAddress();
            var destinationAddress = GetDestinationAddress();
            for (var index = 0; index < CGB_DMA_BLOCK_SIZE; index++)
            {
                _messageBus.WriteCgbDmaDestinationByte(
                    _messageBus.ReadCgbDmaSourceByte(sourceAddress + index),
                    destinationAddress + index);
            }

            SetSourceAddress((sourceAddress + CGB_DMA_BLOCK_SIZE) & 0xFFF0);
            SetDestinationAddress(MemorySchema.VIDEO_RAM_START |
                ((destinationAddress - MemorySchema.VIDEO_RAM_START + CGB_DMA_BLOCK_SIZE) & 0x1FF0));
        }

        /// <summary>
        /// Stores the aligned internal source address used by the next CGB DMA block.
        /// </summary>
        private void SetSourceAddress(int address)
        {
            _sourceHigh = (byte)(address >> 8);
            _sourceLow = (byte)(address & 0xF0);
        }

        /// <summary>
        /// Stores the aligned internal VRAM destination used by the next CGB DMA block.
        /// </summary>
        private void SetDestinationAddress(int address)
        {
            var offset = address - MemorySchema.VIDEO_RAM_START;
            _destinationHigh = (byte)((offset >> 8) & 0x1F);
            _destinationLow = (byte)(offset & 0xF0);
        }

        /// <summary>
        /// Advances an active HBlank transfer by exactly one 16-byte block.
        /// </summary>
        private void OnHBlankDmaWindow()
        {
            if (!_transferring || _messageBus.IsCpuHalted())
            {
                return;
            }

            ScheduleHBlankBlock(writeClocksRemaining: 0);
        }

        /// <summary>
        /// Reserves the CGB memory bus for one 16-byte block at the active CPU speed.
        /// </summary>
        /// <param name="writeClocksRemaining">
        /// Raw clocks in the current write cycle that will advance DMA after the control value becomes visible.
        /// </param>
        private void ScheduleHBlankBlock(int writeClocksRemaining)
        {
            if (_hblankDmaClocksRemaining > 0)
            {
                return;
            }

            _hblankDmaClocksRemaining =
                CGB_DMA_BLOCK_CLOCKS * _messageBus.GetCpuSpeedFactor() +
                CGB_DMA_BLOCK_OVERHEAD_CLOCKS +
                writeClocksRemaining;
        }

        /// <summary>
        /// Copies and accounts for one block of an active HBlank DMA transfer.
        /// </summary>
        private void CopyActiveHBlankBlock()
        {
            CopyBlock();
            var timingEvent = new TimingEvent(TimingEventKind.HBlankDmaBlockCopied);
            _messageBus.ObserveTiming(in timingEvent);

            if (_dmaLengthMode == 0)
            {
                _dmaLengthMode = 0xFF;
                _transferring = false;
            }
            else
            {
                _dmaLengthMode--;
            }
        }
    }
}
