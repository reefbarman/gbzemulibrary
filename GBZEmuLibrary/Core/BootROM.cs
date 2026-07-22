using System;
using System.Collections.Generic;
using System.IO;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Owns the selected model-specific startup firmware image for one emulator instance.
    /// </summary>
    internal sealed class BootROM
    {
        private static readonly byte[] Empty = new byte[0];
        private static readonly Dictionary<HardwareModel, byte[]> BuiltInImages = new Dictionary<HardwareModel, byte[]>();

        [field: SaveStateIgnore]
        public byte[] Bytes { get; private set; } = Empty;

        public byte[] ColorBootROM => IsColorFamilySelected ? Bytes : null;

        public bool HasColorBootROM => IsColorFamilySelected && Bytes.Length != 0;

        public bool IsColorFamilySelected { get; private set; }

        /// <summary>
        /// Clears the active firmware image and overlay mapping.
        /// </summary>
        public void Clear()
        {
            Bytes = Empty;
            IsColorFamilySelected = false;
        }

        /// <summary>
        /// Validates firmware configuration shape without reading files or embedded resources.
        /// </summary>
        public static void ValidateConfig(BootRomConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (!Enum.IsDefined(typeof(BootRomSource), config.Source))
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.Source, "Unknown boot-ROM source.");
            }

            var hasPath = !string.IsNullOrWhiteSpace(config.Path);
            var bytes = config.Bytes;
            var hasBytes = bytes != null;

            switch (config.Source)
            {
                case BootRomSource.BuiltIn:
                case BootRomSource.Skip:
                    if (hasPath || hasBytes)
                    {
                        throw new ArgumentException("Built-in and skip boot-ROM configurations cannot include an external path or byte array.", nameof(config));
                    }
                    break;
                case BootRomSource.External:
                    if (hasPath == hasBytes)
                    {
                        throw new ArgumentException("External boot-ROM configuration requires exactly one path or byte array.", nameof(config));
                    }
                    break;
            }
        }

        /// <summary>
        /// Loads and privately owns the firmware image selected for a concrete model with a prepared firmware slot.
        /// </summary>
        public void Load(HardwareModel model, BootRomConfig config)
        {
            ValidateConfig(config);
            Clear();

            if (config.Source == BootRomSource.Skip)
            {
                return;
            }

            byte[] image;
            if (config.Source == BootRomSource.BuiltIn)
            {
                image = LoadBuiltIn(model);
            }
            else
            {
                var configuredBytes = config.Bytes;
                image = configuredBytes ?? File.ReadAllBytes(config.Path);
            }

            var expectedSize = GetExpectedSize(model);
            if (image.Length != expectedSize)
            {
                throw new ArgumentException(
                    $"Boot ROM for {model} must be exactly {expectedSize} bytes; received {image.Length} bytes.",
                    nameof(config));
            }

            Bytes = (byte[])image.Clone();
            IsColorFamilySelected = model == HardwareModel.CgbE || model == HardwareModel.AgbA;
        }

        private static byte[] LoadBuiltIn(HardwareModel model)
        {
            lock (BuiltInImages)
            {
                if (!BuiltInImages.TryGetValue(model, out var image))
                {
                    var resourceName = GetResourceName(model);
                    image = LoadEmbedded(resourceName, GetExpectedSize(model));
                    BuiltInImages.Add(model, image);
                }

                return image;
            }
        }

        private static int GetExpectedSize(HardwareModel model)
        {
            switch (model)
            {
                case HardwareModel.DmgB:
                case HardwareModel.Mgb:
                case HardwareModel.Sgb2:
                    return 0x100;
                case HardwareModel.CgbE:
                case HardwareModel.AgbA:
                    return 0x900;
                default:
                    throw new InvalidOperationException($"Hardware model {model} does not have an implemented boot-ROM slot.");
            }
        }

        private static string GetResourceName(HardwareModel model)
        {
            switch (model)
            {
                case HardwareModel.DmgB:
                    return "dmg_boot.bin";
                case HardwareModel.Mgb:
                    return "mgb_boot.bin";
                case HardwareModel.CgbE:
                    return "cgb_boot.bin";
                case HardwareModel.AgbA:
                    return "agb_boot.bin";
                case HardwareModel.Sgb2:
                    return "sgb2_boot.bin";
                default:
                    throw new InvalidOperationException($"Hardware model {model} does not have an implemented built-in boot ROM.");
            }
        }

        private static byte[] LoadEmbedded(string name, int expectedSize)
        {
            using (var stream = typeof(BootROM).Assembly.GetManifestResourceStream($"GBZEmuLibrary.Resources.{name}"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException($"Embedded boot ROM resource missing: {name}");
                }

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    var bytes = buffer.ToArray();
                    if (bytes.Length != expectedSize)
                    {
                        throw new InvalidOperationException($"Embedded boot ROM {name} is {bytes.Length} bytes, expected {expectedSize}.");
                    }

                    return bytes;
                }
            }
        }
    }
}
