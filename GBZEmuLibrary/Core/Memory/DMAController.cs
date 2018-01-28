using System;

namespace GBZEmuLibrary
{
    internal class DMAController
    {
        private byte _sourceHigh;
        private byte _sourceLow;

        private byte _destinationHigh;
        private byte _destinationLow;

        private byte _dmaLengthMode;

        private bool _transferring;
        private int _currentIndex;

        public DMAController()
        {
            MessageBus.Instance.OnHBlank += OnHBlank;
        }

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
                        _dmaLengthMode |= 0x80;
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
                MessageBus.Instance.WriteByte(MessageBus.Instance.ReadByte(address + i), MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + i);
            }
        }

        private void StopTransfer()
        {
            _currentIndex = 0;
            _transferring = false;
        }

        private void StartTransfer()
        {
            if (Helpers.TestBit(_dmaLengthMode, 7))
            {
                _currentIndex = 0;
                _transferring = true;
                _dmaLengthMode &= 0x7F;
            }
            else
            {
                for (var i = 0; i < GetLength(); i++)
                {
                    MessageBus.Instance.WriteByte(MessageBus.Instance.ReadByte(GetSourceAddress() + i), GetDestinationAddress() + i);
                }
            }
        }

        private int GetSourceAddress()
        {
            return (_sourceHigh << 8) | (_sourceLow & 0xF0);
        }

        private int GetDestinationAddress()
        {
            return MemorySchema.VIDEO_RAM_START | (((_destinationHigh & 0x1F) << 8) | _destinationLow & 0xF0);
        }

        private int GetLength()
        {
            return ((_dmaLengthMode & 0x7F) + 1) * 0x10;
        }

        private void OnHBlank()
        {
            if (_transferring)
            {
                var offset = _currentIndex * 0x10;
                
                for (var i = 0; i < GetLength(); i++)
                {
                    MessageBus.Instance.WriteByte(MessageBus.Instance.ReadByte(GetSourceAddress() + offset + i), GetDestinationAddress() + offset + i);
                }

                _currentIndex++;

                var next = (_dmaLengthMode & 0x7F) - 1;
                _dmaLengthMode = (byte)((_dmaLengthMode & 0x80) | next);

                if (next <= 0)
                {
                    _dmaLengthMode = 0xFF;
                    _transferring = false;
                }
            }
        }
    }
}
