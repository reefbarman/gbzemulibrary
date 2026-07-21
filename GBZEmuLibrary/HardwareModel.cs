using System;
using System.Collections.Generic;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies the concrete physical Game Boy-family hardware model being emulated.
    /// </summary>
    public enum HardwareModel
    {
        DmgB,
        Mgb,
        CgbE,
        Sgb2,
        AgbA
    }

    /// <summary>
    /// Exposes core-owned hardware implementation and cartridge-compatibility policy to hosts.
    /// </summary>
    public static class HardwareModelMetadata
    {
        private static readonly IReadOnlyList<HardwareModel> Implemented = Array.AsReadOnly(new[]
        {
            HardwareModel.DmgB,
            HardwareModel.CgbE,
            HardwareModel.Sgb2
        });

        /// <summary>
        /// Gets the concrete hardware models implemented by this library build.
        /// </summary>
        public static IReadOnlyList<HardwareModel> ImplementedModels => Implemented;

        /// <summary>
        /// Determines whether the library implements the requested hardware model.
        /// </summary>
        public static bool IsImplemented(HardwareModel model)
        {
            switch (model)
            {
                case HardwareModel.DmgB:
                case HardwareModel.CgbE:
                case HardwareModel.Sgb2:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Determines whether a hardware model can execute a cartridge with the declared compatibility.
        /// Implementation availability is reported separately by <see cref="IsImplemented"/>.
        /// </summary>
        public static bool SupportsCartridge(HardwareModel model, CartridgeCompatibility compatibility)
        {
            if (!Enum.IsDefined(typeof(HardwareModel), model) ||
                !Enum.IsDefined(typeof(CartridgeCompatibility), compatibility))
            {
                return false;
            }

            switch (model)
            {
                case HardwareModel.DmgB:
                case HardwareModel.Mgb:
                case HardwareModel.Sgb2:
                    return compatibility != CartridgeCompatibility.CgbOnly;
                case HardwareModel.CgbE:
                case HardwareModel.AgbA:
                    return true;
                default:
                    return false;
            }
        }
    }
}
