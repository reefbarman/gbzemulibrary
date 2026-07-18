namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies the hardware targeted by a boot-ROM image.
    /// </summary>
    public enum BootRomSystem
    {
        Dmg,
        Cgb
    }

    /// <summary>
    /// Exposes the boot-ROM image-size contract used by the emulator.
    /// </summary>
    public static class BootRomMetadata
    {
        public const int DmgImageSize = 0x100;
        public const int CgbImageSize = 0x900;

        /// <summary>
        /// Classifies a boot-ROM image by its exact byte length.
        /// </summary>
        public static bool TryGetSystem(long imageLength, out BootRomSystem system)
        {
            if (imageLength == DmgImageSize)
            {
                system = BootRomSystem.Dmg;
                return true;
            }

            if (imageLength == CgbImageSize)
            {
                system = BootRomSystem.Cgb;
                return true;
            }

            system = default(BootRomSystem);
            return false;
        }
    }
}
