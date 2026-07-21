using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies where startup firmware comes from for a concrete hardware model.
    /// </summary>
    public enum BootRomSource
    {
        BuiltIn,
        External,
        Skip
    }

    /// <summary>
    /// Describes one immutable firmware choice. The selected hardware model determines the image contract.
    /// </summary>
    public sealed class BootRomConfig
    {
        private readonly byte[] _bytes;

        private BootRomConfig(BootRomSource source, string path, byte[] bytes)
        {
            Source = source;
            Path = path;
            _bytes = bytes == null ? null : (byte[])bytes.Clone();
        }

        /// <summary>
        /// Gets the selected firmware source.
        /// </summary>
        public BootRomSource Source { get; }

        /// <summary>
        /// Gets the external firmware path, or null when firmware is built in, byte-backed, or skipped.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets a private copy of the external firmware bytes, or null when no byte-backed image was configured.
        /// </summary>
        public byte[] Bytes => _bytes == null ? null : (byte[])_bytes.Clone();

        /// <summary>
        /// Selects the embedded open GBZEmu firmware for the configured hardware model.
        /// </summary>
        public static BootRomConfig BuiltIn()
        {
            return new BootRomConfig(BootRomSource.BuiltIn, null, null);
        }

        /// <summary>
        /// Selects an external firmware file for the configured hardware model.
        /// </summary>
        public static BootRomConfig ExternalFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An external boot-ROM path is required.", nameof(path));
            }

            return new BootRomConfig(BootRomSource.External, path, null);
        }

        /// <summary>
        /// Selects an external firmware image for the configured hardware model and takes a private copy.
        /// </summary>
        public static BootRomConfig ExternalBytes(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return new BootRomConfig(BootRomSource.External, null, bytes);
        }

        /// <summary>
        /// Skips firmware execution and applies the model- and cartridge-specific deterministic handoff state.
        /// </summary>
        public static BootRomConfig Skip()
        {
            return new BootRomConfig(BootRomSource.Skip, null, null);
        }
    }
}
