using System.IO;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Provides file-backed cartridge RAM while preserving optional controller metadata appended after raw RAM bytes.
    /// </summary>
    internal class ExternalRAM : IStateSerializable
    {
        public bool Enabled
        {
            get => _enabled;

            set
            {
                _enabled = value;
                _externalRAM.Flush();
            }
        }

        public int Length { get; }

        private readonly FileStream _externalRAM;
        private bool _enabled;

        public ExternalRAM(string saveLocation, string romName, int externalRAMSize)
        {
            saveLocation = !string.IsNullOrEmpty(saveLocation) ? saveLocation : Directory.GetCurrentDirectory();

            var path = Path.Combine(saveLocation, $"{romName}.sav");

            if (File.Exists(path))
            {
                _externalRAM = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            }
            else
            {
                _externalRAM = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
                for (var i = 0; i < externalRAMSize; i++)
                {
                    _externalRAM.WriteByte(0xFF);
                }

                _externalRAM.Flush();
            }

            Length = externalRAMSize;
        }

        /// <summary>
        /// Reads a recognized 44- or 48-byte RTC trailer stored immediately after the declared RAM region.
        /// </summary>
        public byte[] ReadRTCTrailer()
        {
            var trailerLength = _externalRAM.Length - Length;
            if (trailerLength != 44 && trailerLength != MBC3RTC.PersistenceSize)
            {
                return null;
            }

            var data = new byte[trailerLength];
            _externalRAM.Position = Length;
            var offset = 0;
            while (offset < data.Length)
            {
                var read = _externalRAM.Read(data, offset, data.Length - offset);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }

            return data;
        }

        /// <summary>
        /// Writes a normalized RTC trailer after raw RAM without changing the RAM prefix or save filename.
        /// </summary>
        public void WriteRTCTrailer(byte[] data)
        {
            _externalRAM.Position = Length;
            _externalRAM.Write(data, 0, data.Length);
            _externalRAM.SetLength(Length + data.Length);
            _externalRAM.Flush();
        }

        public void Dispose()
        {
            _externalRAM?.Flush();
            _externalRAM?.Close();
        }

        public void WriteByte(byte data, int address)
        {
            _externalRAM.Position = address;
            _externalRAM.WriteByte(data);
        }

        public byte ReadByte(int address)
        {
            _externalRAM.Position = address;
            return (byte)_externalRAM.ReadByte();
        }

        /// <summary>
        /// Captures raw battery-backed RAM and the active RAM-enable latch without including persistence metadata.
        /// </summary>
        public void WriteState(BinaryWriter writer)
        {
            writer.Write(Length);
            writer.Write(_enabled);

            var originalPosition = _externalRAM.Position;
            _externalRAM.Position = 0;
            var data = new byte[Length];
            var offset = 0;
            while (offset < data.Length)
            {
                var read = _externalRAM.Read(data, offset, data.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("Cartridge RAM ended before its declared size.");
                }

                offset += read;
            }

            _externalRAM.Position = originalPosition;
            writer.Write(data);
        }

        /// <summary>
        /// Restores raw battery-backed RAM in place so subsequent cartridge writes continue using the same save file.
        /// </summary>
        public void ReadState(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length != Length)
            {
                throw new InvalidDataException("Save state cartridge RAM size does not match the loaded ROM.");
            }

            _enabled = reader.ReadBoolean();
            var data = reader.ReadBytes(length);
            if (data.Length != length)
            {
                throw new EndOfStreamException("Save state ended inside cartridge RAM.");
            }

            _externalRAM.Position = 0;
            _externalRAM.Write(data, 0, data.Length);
            _externalRAM.Flush();
        }
    }
}
