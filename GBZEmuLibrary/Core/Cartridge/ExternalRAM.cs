using System.IO;

namespace GBZEmuLibrary
{
    internal class ExternalRAM
    {
        public bool Enabled
        {
            get
            {
                return _enabled;
            }

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
    }
}
