using System;
using System.Collections.Generic;
using System.IO;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies a supported binary ROM-patch format.
    /// </summary>
    public enum RomPatchFormat
    {
        Ips,
        Bps
    }

    /// <summary>
    /// Reports malformed, unsupported, or unsafe ROM-patch data with optional format and stack-position context.
    /// </summary>
    public sealed class RomPatchException : IOException
    {
        internal RomPatchException(string message, RomPatchFormat? format = null, int? patchIndex = null, Exception innerException = null)
            : base(message, innerException)
        {
            Format = format;
            PatchIndex = patchIndex;
        }

        /// <summary>
        /// Gets the detected patch format, or null when the patch magic was not recognized.
        /// </summary>
        public RomPatchFormat? Format { get; }

        /// <summary>
        /// Gets the one-based failing patch position when applying a stack, or null for a single patch.
        /// </summary>
        public int? PatchIndex { get; }
    }

    /// <summary>
    /// Detects and applies supported binary patches without mutating caller-owned source or patch arrays.
    /// </summary>
    public static class RomPatcher
    {
        internal const int MaximumPatchSize = 64 * 1024 * 1024;
        internal const int MaximumOutputSize = CartridgeSchema.MAX_CART_SIZE;

        /// <summary>
        /// Detects a patch format from its file magic rather than its filename or extension.
        /// </summary>
        public static RomPatchFormat DetectFormat(byte[] patch)
        {
            if (patch == null)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            if (HasMagic(patch, "PATCH"))
            {
                return RomPatchFormat.Ips;
            }

            if (HasMagic(patch, "BPS1"))
            {
                return RomPatchFormat.Bps;
            }

            throw new RomPatchException("The patch does not contain supported IPS or BPS magic.");
        }

        /// <summary>
        /// Applies one detected patch and returns a new target image.
        /// </summary>
        public static byte[] Apply(byte[] source, byte[] patch)
        {
            ValidateSource(source);
            ValidatePatchSize(patch);
            var format = DetectFormat(patch);
            switch (format)
            {
                case RomPatchFormat.Ips:
                    return IpsPatch.Apply(source, patch);
                case RomPatchFormat.Bps:
                    return BpsPatch.Apply(source, patch);
                default:
                    throw new RomPatchException("The detected patch format is not supported.", format);
            }
        }

        /// <summary>
        /// Applies patches in order, using each output as the next patch's source, and returns a new final image.
        /// </summary>
        public static byte[] Apply(byte[] source, IReadOnlyList<byte[]> patches)
        {
            ValidateSource(source);
            if (patches == null)
            {
                throw new ArgumentNullException(nameof(patches));
            }

            var output = (byte[])source.Clone();
            for (var index = 0; index < patches.Count; index++)
            {
                try
                {
                    output = Apply(output, patches[index]);
                }
                catch (RomPatchException exception)
                {
                    var oneBasedIndex = index + 1;
                    throw new RomPatchException(
                        $"Patch {oneBasedIndex} failed: {exception.Message}",
                        exception.Format,
                        oneBasedIndex,
                        exception);
                }
                catch (ArgumentNullException exception)
                {
                    var oneBasedIndex = index + 1;
                    throw new RomPatchException(
                        $"Patch {oneBasedIndex} was null.",
                        null,
                        oneBasedIndex,
                        exception);
                }
            }

            return output;
        }

        internal static void ValidateSource(byte[] source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.Length > MaximumOutputSize)
            {
                throw new RomPatchException(
                    $"The source exceeds the supported {MaximumOutputSize / (1024 * 1024)} MiB ROM limit.");
            }
        }

        internal static void ValidatePatchSize(byte[] patch)
        {
            if (patch == null)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            if (patch.Length > MaximumPatchSize)
            {
                throw new RomPatchException(
                    $"The patch exceeds the supported {MaximumPatchSize / (1024 * 1024)} MiB limit.");
            }
        }

        private static bool HasMagic(byte[] data, string magic)
        {
            if (data.Length < magic.Length)
            {
                return false;
            }

            for (var index = 0; index < magic.Length; index++)
            {
                if (data[index] != (byte)magic[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
