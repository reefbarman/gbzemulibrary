using System;
using System.IO;
using System.Security.Cryptography;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Represents a versioned, engine-neutral snapshot of one running emulator's complete mutable machine state.
    /// </summary>
    public sealed class EmulatorState
    {
        internal const int CurrentFormatVersion = 1;

        private readonly byte[] _data;

        /// <summary>
        /// Gets the binary format version used by this state.
        /// </summary>
        public int FormatVersion { get; }

        /// <summary>
        /// Gets the serialized size in bytes, useful when sizing rewind history.
        /// </summary>
        public int SerializedLength => _data.Length;

        internal byte[] Data => _data;

        internal EmulatorState(byte[] data, int formatVersion)
        {
            _data = data;
            FormatVersion = formatVersion;
        }

        /// <summary>
        /// Creates a state object from bytes previously returned by <see cref="ToArray"/>.
        /// Cartridge and boot-ROM compatibility are checked when the state is restored.
        /// </summary>
        public static EmulatorState FromArray(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var copy = (byte[])data.Clone();
            var parsed = StateEnvelope.Parse(copy, null);
            return new EmulatorState(copy, parsed.FormatVersion);
        }

        /// <summary>
        /// Returns a private copy suitable for storing on disk or transferring between engine-neutral hosts.
        /// </summary>
        public byte[] ToArray()
        {
            return (byte[])_data.Clone();
        }
    }

    /// <summary>
    /// Protects state payloads with format, cartridge/firmware identity, length, and checksum validation.
    /// </summary>
    internal static class StateEnvelope
    {
        private static readonly byte[] Magic = { (byte)'G', (byte)'B', (byte)'Z', (byte)'S', (byte)'T', (byte)'A', (byte)'T', (byte)'E' };
        private const int IdentityLength = 32;
        private const int ChecksumLength = 32;

        internal sealed class ParsedState
        {
            public int FormatVersion;
            public byte[] Payload;
        }

        public static EmulatorState Create(byte[] identity, byte[] payload)
        {
            if (identity == null || identity.Length != IdentityLength)
            {
                throw new ArgumentException("State identity must be a SHA-256 hash.", nameof(identity));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(EmulatorState.CurrentFormatVersion);
                writer.Write(identity);
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Flush();

                var body = stream.ToArray();
                byte[] checksum;
                using (var sha256 = SHA256.Create())
                {
                    checksum = sha256.ComputeHash(body);
                }

                writer.Write(checksum);
                writer.Flush();
                return new EmulatorState(stream.ToArray(), EmulatorState.CurrentFormatVersion);
            }
        }

        public static ParsedState Parse(byte[] data, byte[] expectedIdentity)
        {
            var minimumLength = Magic.Length + sizeof(int) + IdentityLength + sizeof(int) + ChecksumLength;
            if (data.Length < minimumLength)
            {
                throw new InvalidDataException("Save state is truncated.");
            }

            var bodyLength = data.Length - ChecksumLength;
            byte[] calculatedChecksum;
            using (var sha256 = SHA256.Create())
            {
                calculatedChecksum = sha256.ComputeHash(data, 0, bodyLength);
            }

            for (var index = 0; index < ChecksumLength; index++)
            {
                if (calculatedChecksum[index] != data[bodyLength + index])
                {
                    throw new InvalidDataException("Save state checksum is invalid.");
                }
            }

            using (var stream = new MemoryStream(data, 0, bodyLength, false))
            using (var reader = new BinaryReader(stream))
            {
                var magic = reader.ReadBytes(Magic.Length);
                for (var index = 0; index < Magic.Length; index++)
                {
                    if (magic[index] != Magic[index])
                    {
                        throw new InvalidDataException("File is not a GBZEmu save state.");
                    }
                }

                var version = reader.ReadInt32();
                if (version != EmulatorState.CurrentFormatVersion)
                {
                    throw new NotSupportedException("Save state format version " + version + " is not supported.");
                }

                var identity = reader.ReadBytes(IdentityLength);
                if (identity.Length != IdentityLength)
                {
                    throw new InvalidDataException("Save state identity is truncated.");
                }

                if (expectedIdentity != null)
                {
                    for (var index = 0; index < IdentityLength; index++)
                    {
                        if (identity[index] != expectedIdentity[index])
                        {
                            throw new InvalidOperationException("Save state belongs to a different ROM or boot-ROM configuration.");
                        }
                    }
                }

                var payloadLength = reader.ReadInt32();
                if (payloadLength < 0 || payloadLength != bodyLength - stream.Position)
                {
                    throw new InvalidDataException("Save state payload length is invalid.");
                }

                var payload = reader.ReadBytes(payloadLength);
                if (payload.Length != payloadLength)
                {
                    throw new InvalidDataException("Save state payload is truncated.");
                }

                return new ParsedState
                {
                    FormatVersion = version,
                    Payload = payload
                };
            }
        }
    }
}
