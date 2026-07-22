namespace GBZEmuLibrary
{
    /// <summary>
    /// Describes the documented cartridge-entry state produced by one concrete hardware startup path.
    /// </summary>
    internal sealed class HardwareStartupProfile
    {
        private const ushort CartridgeEntryPoint = 0x0100;
        private const ushort InitialStackPointer = 0xFFFE;

        private HardwareStartupProfile(
            HardwareModel hardwareModel,
            GBCMode executionMode,
            ushort af,
            ushort bc,
            ushort de,
            ushort hl,
            byte key0,
            byte objectPriority,
            bool installCompatibilityPalettes)
        {
            HardwareModel = hardwareModel;
            ExecutionMode = executionMode;
            AF = af;
            BC = bc;
            DE = de;
            HL = hl;
            SP = InitialStackPointer;
            PC = CartridgeEntryPoint;
            Key0 = key0;
            ObjectPriority = objectPriority;
            InstallCompatibilityPalettes = installCompatibilityPalettes;
        }

        public HardwareModel HardwareModel { get; }
        public GBCMode ExecutionMode { get; }
        public ushort AF { get; }
        public ushort BC { get; }
        public ushort DE { get; }
        public ushort HL { get; }
        public ushort SP { get; }
        public ushort PC { get; }
        public byte Key0 { get; }
        public byte ObjectPriority { get; }
        public bool InstallCompatibilityPalettes { get; }

        /// <summary>
        /// Resolves the later AGB boot handoff from cartridge mode, licensee, and title bytes.
        /// </summary>
        public static HardwareStartupProfile ResolveAgbA(CartridgeHeader header)
        {
            if (header.GBCMode != GBCMode.NoGBC)
            {
                return new HardwareStartupProfile(
                    HardwareModel.AgbA,
                    header.GBCMode,
                    0x1100,
                    0x0100,
                    0xFF56,
                    0x000D,
                    header.CgbFlag,
                    0x00,
                    false);
            }

            var beforeIncrement = header.IsNintendoLicensed ? header.TitleChecksum : (byte)0;
            var b = (byte)(beforeIncrement + 1);
            byte flags = 0;
            if (b == 0)
            {
                flags |= 1 << InstructionSchema.FLAG_Z;
            }

            if ((beforeIncrement & 0x0F) == 0x0F)
            {
                flags |= 1 << InstructionSchema.FLAG_H;
            }

            return new HardwareStartupProfile(
                HardwareModel.AgbA,
                GBCMode.GBCCompatibility,
                (ushort)(0x1100 | flags),
                (ushort)(b << 8),
                0x0008,
                b == 0x44 || b == 0x59 ? (ushort)0x991A : (ushort)0x007C,
                0x04,
                0x01,
                true);
        }
    }
}
