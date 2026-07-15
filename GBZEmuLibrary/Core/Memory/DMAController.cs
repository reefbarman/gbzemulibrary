using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates OAM DMA and the CGB general-purpose/HBlank DMA engines that copy data into VRAM.
    /// </summary>
    internal class DMAController : IMemoryUnit
    {
        private const int CGB_DMA_BLOCK_SIZE = 0x10;

        private byte _sourceHigh;
        private byte _sourceLow;

        private byte _destinationHigh;
        private byte _destinationLow;

        private byte _dmaLengthMode;

        private bool _transferring;
        private int _currentIndex;

        private readonly MessageBus _messageBus;

        /// <summary>
        /// Creates a DMA controller connected to the memory and HBlank bus for its owning emulator.
        /// </summary>
        public DMAController(MessageBus messageBus)
        {
            _messageBus = messageBus;
            _messageBus.OnHBlank = OnHBlank;
        }

        /// <summary>
        /// Updates an OAM or CGB VRAM DMA register and starts or stops a transfer when its control register is written.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            switch (address)
            {
                case MemorySchema.DMA_REGISTER:
                    ProcessDMATranser(data);
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

                    if (_transferring && !Helpers.TestBit(data, 7))
                    {
                        StopTransfer();
                    }
                    else
                    {
                        _dmaLengthMode = data;
                        StartTransfer();
                    }

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

        public byte ReadByte(int address)
        {
            switch (address)
            {
                case MemorySchema.DMA_REGISTER:
                    return 0;

                case MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER:
                    return _sourceHigh;

                case MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER:
                    return _sourceLow;

                case MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER:
                    return _destinationHigh;

                case MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER:
                    return _destinationLow;

                case MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER:
                    return _dmaLengthMode;
            }

            throw new IndexOutOfRangeException();
        }

        private void ProcessDMATranser(byte data)
        {
            var address = data << 8;

            for (var i = 0; i < (MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START); i++)
            {
                _messageBus.WriteByte(_messageBus.ReadByte(address + i), MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + i);
            }
        }

        /// <summary>
        /// Cancels an active HBlank transfer while preserving its remaining-block readback in HDMA5.
        /// </summary>
        private void StopTransfer()
        {
            // Preserve the remaining-block value for inactive HDMA5 readback; a later transfer resets its own index.
            _dmaLengthMode |= 0x80;
            _transferring = false;
        }

        /// <summary>
        /// Starts HDMA or completes GDMA immediately. CPU stall timing is not yet modeled.
        /// </summary>
        private void StartTransfer()
        {
            _currentIndex = 0;
            var hBlankMode = Helpers.TestBit(_dmaLengthMode, 7);
            _dmaLengthMode &= 0x7F;

            if (hBlankMode)
            {
                _transferring = true;
                return;
            }

            var blockCount = _dmaLengthMode + 1;
            for (var block = 0; block < blockCount; block++)
            {
                CopyBlock(block);
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
        /// Copies one 16-byte CGB DMA block through the MMU so bank selection and device side effects are preserved.
        /// </summary>
        private void CopyBlock(int blockIndex)
        {
            var offset = blockIndex * CGB_DMA_BLOCK_SIZE;
            for (var index = 0; index < CGB_DMA_BLOCK_SIZE; index++)
            {
                _messageBus.WriteByte(
                    _messageBus.ReadByte(GetSourceAddress() + offset + index),
                    GetDestinationAddress() + offset + index);
            }
        }

        /// <summary>
        /// Advances an active HBlank transfer by exactly one 16-byte block.
        /// </summary>
        private void OnHBlank()
        {
            if (!_transferring)
            {
                return;
            }

            CopyBlock(_currentIndex);
            _currentIndex++;

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
