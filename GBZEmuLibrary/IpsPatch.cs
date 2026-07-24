using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Applies International Patching System records to one bounded in-memory source image.
    /// </summary>
    internal static class IpsPatch
    {
        private const int HeaderLength = 5;
        private const int EndMarkerLength = 3;
        private const int RlePayloadLength = 3;
        private const int EndMarkerOffset = 0x454F46;

        private sealed class Layout
        {
            public int EndMarkerPosition;
            public int AllocationSize;
            public int FinalSize;
        }

        public static byte[] Apply(byte[] source, byte[] patch)
        {
            var layout = ParseAndValidate(source.Length, patch);
            var output = new byte[layout.AllocationSize];
            Buffer.BlockCopy(source, 0, output, 0, source.Length);
            ApplyRecords(patch, layout.EndMarkerPosition, output);

            if (layout.FinalSize == output.Length)
            {
                return output;
            }

            var resized = new byte[layout.FinalSize];
            Buffer.BlockCopy(output, 0, resized, 0, Math.Min(output.Length, resized.Length));
            return resized;
        }

        private static Layout ParseAndValidate(int sourceLength, byte[] patch)
        {
            var position = HeaderLength;
            var maximumRecordEnd = sourceLength;
            while (true)
            {
                EnsureAvailable(patch, position, EndMarkerLength, "IPS patch ended before its EOF marker.");
                var recordOffset = ReadUInt24BigEndian(patch, position);
                position += 3;
                if (recordOffset == EndMarkerOffset)
                {
                    break;
                }

                EnsureAvailable(patch, position, sizeof(ushort), "IPS patch ended inside a record size.");
                var recordSize = ReadUInt16BigEndian(patch, position);
                position += 2;
                int writeLength;
                if (recordSize == 0)
                {
                    EnsureAvailable(patch, position, RlePayloadLength, "IPS patch ended inside an RLE record.");
                    writeLength = ReadUInt16BigEndian(patch, position);
                    position += RlePayloadLength;
                    if (writeLength == 0)
                    {
                        throw Error("IPS RLE records must write at least one byte.");
                    }
                }
                else
                {
                    writeLength = recordSize;
                    EnsureAvailable(patch, position, writeLength, "IPS patch ended inside a record payload.");
                    position += writeLength;
                }

                var recordEnd = ValidateRange(recordOffset, writeLength);
                if (recordEnd > maximumRecordEnd)
                {
                    maximumRecordEnd = recordEnd;
                }
            }

            var endMarkerPosition = position;
            var trailingBytes = patch.Length - position;
            int finalSize;
            if (trailingBytes == 0)
            {
                finalSize = maximumRecordEnd;
            }
            else if (trailingBytes == 3)
            {
                finalSize = ReadUInt24BigEndian(patch, position);
                ValidateTargetSize(finalSize);
            }
            else
            {
                throw Error("IPS patch contains unsupported data after its EOF marker.");
            }

            return new Layout
            {
                EndMarkerPosition = endMarkerPosition,
                AllocationSize = Math.Max(maximumRecordEnd, finalSize),
                FinalSize = finalSize
            };
        }

        private static void ApplyRecords(byte[] patch, int endMarkerPosition, byte[] output)
        {
            var position = HeaderLength;
            while (position < endMarkerPosition - EndMarkerLength)
            {
                var recordOffset = ReadUInt24BigEndian(patch, position);
                position += 3;
                var recordSize = ReadUInt16BigEndian(patch, position);
                position += 2;
                if (recordSize == 0)
                {
                    var runLength = ReadUInt16BigEndian(patch, position);
                    var value = patch[position + 2];
                    position += RlePayloadLength;
                    for (var index = 0; index < runLength; index++)
                    {
                        output[recordOffset + index] = value;
                    }
                }
                else
                {
                    Buffer.BlockCopy(patch, position, output, recordOffset, recordSize);
                    position += recordSize;
                }
            }
        }

        private static int ValidateRange(int offset, int length)
        {
            int requiredLength;
            try
            {
                requiredLength = checked(offset + length);
            }
            catch (OverflowException exception)
            {
                throw Error("IPS record range overflowed the supported address space.", exception);
            }

            ValidateTargetSize(requiredLength);
            return requiredLength;
        }

        private static void ValidateTargetSize(int size)
        {
            if (size > RomPatcher.MaximumOutputSize)
            {
                throw Error($"IPS target exceeds the supported {RomPatcher.MaximumOutputSize / (1024 * 1024)} MiB ROM limit.");
            }
        }

        private static void EnsureAvailable(byte[] patch, int offset, int length, string message)
        {
            if (offset < 0 || length < 0 || offset > patch.Length - length)
            {
                throw Error(message);
            }
        }

        private static int ReadUInt24BigEndian(byte[] bytes, int offset)
        {
            return (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
        }

        private static int ReadUInt16BigEndian(byte[] bytes, int offset)
        {
            return (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static RomPatchException Error(string message, Exception innerException = null)
        {
            return new RomPatchException(message, RomPatchFormat.Ips, null, innerException);
        }
    }
}
