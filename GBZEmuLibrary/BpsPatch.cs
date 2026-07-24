using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Applies BPS1 linear actions with source, target, and patch CRC validation.
    /// </summary>
    internal static class BpsPatch
    {
        private const int HeaderLength = 4;
        private const int FooterLength = 12;

        public static byte[] Apply(byte[] source, byte[] patch)
        {
            if (patch.Length < HeaderLength + 3 + FooterLength)
            {
                throw Error("BPS patch is too short to contain its header, size fields, and footer.");
            }

            var footerPosition = patch.Length - FooterLength;
            var expectedSourceCrc = ReadUInt32LittleEndian(patch, footerPosition);
            var expectedTargetCrc = ReadUInt32LittleEndian(patch, footerPosition + 4);
            var expectedPatchCrc = ReadUInt32LittleEndian(patch, footerPosition + 8);
            var actualPatchCrc = Crc32.Compute(patch, 0, patch.Length - 4);
            if (actualPatchCrc != expectedPatchCrc)
            {
                throw Error($"BPS patch checksum mismatch: expected {expectedPatchCrc:X8}, actual {actualPatchCrc:X8}.");
            }

            var position = HeaderLength;
            var sourceSize = ReadNumber(patch, ref position, footerPosition, "source size");
            var targetSize = ReadNumber(patch, ref position, footerPosition, "target size");
            var metadataSize = ReadNumber(patch, ref position, footerPosition, "metadata size");
            if (sourceSize != (ulong)source.Length)
            {
                throw Error($"BPS source size mismatch: expected {sourceSize}, actual {source.Length}.");
            }

            if (targetSize > RomPatcher.MaximumOutputSize)
            {
                throw Error($"BPS target exceeds the supported {RomPatcher.MaximumOutputSize / (1024 * 1024)} MiB ROM limit.");
            }

            if (metadataSize > (ulong)(footerPosition - position))
            {
                throw Error("BPS metadata extends into the checksum footer.");
            }

            position += (int)metadataSize;
            var actualSourceCrc = Crc32.Compute(source, 0, source.Length);
            if (actualSourceCrc != expectedSourceCrc)
            {
                throw Error($"BPS source checksum mismatch: expected {expectedSourceCrc:X8}, actual {actualSourceCrc:X8}.");
            }

            var output = new byte[(int)targetSize];
            var outputOffset = 0;
            long sourceRelativeOffset = 0;
            long targetRelativeOffset = 0;
            while (outputOffset < output.Length)
            {
                var actionData = ReadNumber(patch, ref position, footerPosition, "action");
                var action = (int)(actionData & 3);
                var lengthValue = (actionData >> 2) + 1;
                if (lengthValue > (ulong)(output.Length - outputOffset))
                {
                    throw Error("BPS action writes beyond the declared target size.");
                }

                var length = (int)lengthValue;
                switch (action)
                {
                    case 0:
                        if (outputOffset > source.Length - length)
                        {
                            throw Error("BPS SourceRead action reads beyond the source image.");
                        }

                        Buffer.BlockCopy(source, outputOffset, output, outputOffset, length);
                        outputOffset += length;
                        break;
                    case 1:
                        EnsurePatchBytes(position, length, footerPosition, "BPS TargetRead action extends into the checksum footer.");
                        Buffer.BlockCopy(patch, position, output, outputOffset, length);
                        position += length;
                        outputOffset += length;
                        break;
                    case 2:
                        sourceRelativeOffset = AdjustRelativeOffset(
                            sourceRelativeOffset,
                            ReadNumber(patch, ref position, footerPosition, "SourceCopy offset"),
                            "source");
                        if (sourceRelativeOffset < 0 || sourceRelativeOffset > source.Length - length)
                        {
                            throw Error("BPS SourceCopy action reads outside the source image.");
                        }

                        Buffer.BlockCopy(source, (int)sourceRelativeOffset, output, outputOffset, length);
                        sourceRelativeOffset += length;
                        outputOffset += length;
                        break;
                    case 3:
                        targetRelativeOffset = AdjustRelativeOffset(
                            targetRelativeOffset,
                            ReadNumber(patch, ref position, footerPosition, "TargetCopy offset"),
                            "target");
                        if (targetRelativeOffset < 0 || targetRelativeOffset >= outputOffset)
                        {
                            throw Error("BPS TargetCopy action begins outside previously written target data.");
                        }

                        for (var index = 0; index < length; index++)
                        {
                            if (targetRelativeOffset < 0 || targetRelativeOffset >= outputOffset)
                            {
                                throw Error("BPS TargetCopy action reads beyond available target data.");
                            }

                            output[outputOffset++] = output[targetRelativeOffset++];
                        }

                        break;
                    default:
                        throw Error("BPS action type is invalid.");
                }
            }

            if (position != footerPosition)
            {
                throw Error("BPS action stream does not end at the checksum footer.");
            }

            var actualTargetCrc = Crc32.Compute(output, 0, output.Length);
            if (actualTargetCrc != expectedTargetCrc)
            {
                throw Error($"BPS target checksum mismatch: expected {expectedTargetCrc:X8}, actual {actualTargetCrc:X8}.");
            }

            return output;
        }

        private static ulong ReadNumber(byte[] patch, ref int position, int limit, string fieldName)
        {
            ulong data = 0;
            ulong shift = 1;
            while (true)
            {
                if (position >= limit)
                {
                    throw Error($"BPS patch ended inside its {fieldName} value.");
                }

                var value = patch[position++];
                var part = (ulong)(value & 0x7F);
                if (part != 0 && shift > ulong.MaxValue / part)
                {
                    throw Error($"BPS {fieldName} value overflowed 64 bits.");
                }

                var contribution = part * shift;
                if (data > ulong.MaxValue - contribution)
                {
                    throw Error($"BPS {fieldName} value overflowed 64 bits.");
                }

                data += contribution;
                if ((value & 0x80) != 0)
                {
                    return data;
                }

                if (shift > ulong.MaxValue >> 7)
                {
                    throw Error($"BPS {fieldName} value overflowed 64 bits.");
                }

                shift <<= 7;
                if (data > ulong.MaxValue - shift)
                {
                    throw Error($"BPS {fieldName} value overflowed 64 bits.");
                }

                data += shift;
            }
        }

        private static long AdjustRelativeOffset(long current, ulong encodedOffset, string cursorName)
        {
            var magnitude = encodedOffset >> 1;
            if (magnitude > long.MaxValue)
            {
                throw Error($"BPS {cursorName} relative offset exceeded the supported range.");
            }

            var signedMagnitude = (long)magnitude;
            try
            {
                return (encodedOffset & 1) != 0
                    ? checked(current - signedMagnitude)
                    : checked(current + signedMagnitude);
            }
            catch (OverflowException exception)
            {
                throw Error($"BPS {cursorName} relative offset overflowed.", exception);
            }
        }

        private static void EnsurePatchBytes(int position, int length, int limit, string message)
        {
            if (position < 0 || length < 0 || position > limit - length)
            {
                throw Error(message);
            }
        }

        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }

        private static RomPatchException Error(string message, Exception innerException = null)
        {
            return new RomPatchException(message, RomPatchFormat.Bps, null, innerException);
        }
    }
}
