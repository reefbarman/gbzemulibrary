namespace GBZEmuLibrary
{
    /// <summary>
    /// Owns the color-family boot-time KEY0 mode latch and OPRI object-priority policy.
    /// </summary>
    internal sealed class CompatibilityModeRegisters : IMemoryUnit
    {
        private HardwareModel _hardwareModel;
        private byte _key0;
        private byte _objectPriority;
        private bool _dmgObjectPriority;
        private bool _locked;

        /// <summary>
        /// Gets whether the latched OPRI mode resolves overlapping objects by X coordinate.
        /// </summary>
        public bool UsesDmgObjectPriority => _dmgObjectPriority;

        /// <summary>
        /// Initializes model visibility and clears the boot-time latches for a new run.
        /// </summary>
        public void Init(HardwareModel hardwareModel, bool useDmgObjectPriority)
        {
            _hardwareModel = hardwareModel;
            _key0 = 0;
            _objectPriority = useDmgObjectPriority ? (byte)1 : (byte)0;
            _dmgObjectPriority = useDmgObjectPriority;
            _locked = false;
        }

        /// <summary>
        /// Applies the resolved skip-boot register values and locks their effective behavior at cartridge entry.
        /// </summary>
        public void ApplyStartupProfile(HardwareStartupProfile profile)
        {
            ApplyHandoff(profile.Key0, profile.ObjectPriority);
        }

        /// <summary>
        /// Applies a complete skip-boot handoff and locks its effective object-priority behavior.
        /// </summary>
        public void ApplyHandoff(byte key0, byte objectPriority)
        {
            _key0 = key0;
            _objectPriority = (byte)(objectPriority & 0x01);
            _dmgObjectPriority = (_objectPriority & 0x01) != 0;
            _locked = true;
        }

        /// <summary>
        /// Locks boot-time mode selection when firmware unmaps itself.
        /// </summary>
        public void Lock()
        {
            _dmgObjectPriority = (_objectPriority & 0x01) != 0;
            _locked = true;
        }

        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.CPU_MODE_SELECT_REGISTER ||
                   address == MemorySchema.OBJECT_PRIORITY_REGISTER;
        }

        public byte ReadByte(int address)
        {
            if (!IsColorFamilyHardware())
            {
                return 0xFF;
            }

            switch (address)
            {
                case MemorySchema.CPU_MODE_SELECT_REGISTER:
                    return _key0;
                case MemorySchema.OBJECT_PRIORITY_REGISTER:
                    return (byte)(_objectPriority | 0xFE);
                default:
                    return 0xFF;
            }
        }

        public void WriteByte(byte data, int address)
        {
            if (!IsColorFamilyHardware())
            {
                return;
            }

            switch (address)
            {
                case MemorySchema.CPU_MODE_SELECT_REGISTER:
                    if (!_locked)
                    {
                        _key0 = data;
                    }
                    break;
                case MemorySchema.OBJECT_PRIORITY_REGISTER:
                    _objectPriority = (byte)(data & 0x01);
                    if (!_locked)
                    {
                        _dmgObjectPriority = _objectPriority != 0;
                    }
                    break;
            }
        }

        private bool IsColorFamilyHardware()
        {
            return _hardwareModel == HardwareModel.CgbE || _hardwareModel == HardwareModel.AgbA;
        }
    }
}
