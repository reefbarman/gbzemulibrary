using System;
using System.Linq;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the DMG/CGB pixel-processing unit, including VRAM, OAM, LCD registers, and scanline rendering.
    /// </summary>
    internal class GPU : IMemoryUnit
    {
        private enum LCDStatus
        {
            HBlank,
            VBlank,
            SearchingSpritesAttributes,
            TransferringDataToLCDDriver
        }

        private enum Registers
        {
            LCDControl,
            LCDStatus,
            ScrollY,
            ScrollX,
            Scanline,
            LCDYCoord,
            DMA,
            BackgroundTilePalette,
            SpritePalette0,
            SpritePalette1,
            WindowY,
            WindowX
        }

        private enum LCDStatusBits
        {
            Mode0,
            Mode1,
            Coincidence,
            HBlankInterruptEnabled,
            VBlankInterruptEnabled,
            SearchingSpriteAttributesInterruptEnabled,
            CoincidenceInterruptEnabled,
            Unknown
        }

        private enum LCDControlBits
        {
            BGDisplayEnabled,
            SpriteDisplayEnabled,
            SpriteSize,
            BGTileMapSelect,
            BGWindowTileDataSelect,
            WindowDisplayEnabled,
            WindowTileMapSelect,
            LCDDisplayEnabled,
        }

        private enum SpriteAttributesBits
        {
            Palette0,
            Palette1,
            Palette2,
            TileVRAMBankNumber,
            PaletteNum,
            XFlip,
            YFlip,
            SpriteToBGPriority
        }

        private enum BGAttributeBits
        {
            Palette0,
            Palette1,
            Palette2,
            TileVRAMBankNumber,
            Unused,
            XFlip,
            YFlip,
            BGToSpritePriority
        }

        private int ScanLine
        {
            get
            {
                return _gpuRegisters[(int)Registers.Scanline];
            }

            set
            {
                _gpuRegisters[(int)Registers.Scanline] = (byte)value;
                UpdateCoincidenceFlag();
            }
        }

        private const int SCANLINE_DRAW_CLOCKS = 456; //TODO maybe use floats and get more accuracy as this should be more like 456.8 for 60FPS
        private const int HBLANK_CLOCKS = 196;
        private const int VBLANK_ENTRY_HBLANK_CLOCKS = 200;
        private const int MODE2_START_DELAY_CLOCKS = 8;
        private const int FRAME_START_MODE2_DELAY_CLOCKS = 4;
        private const int CGB_COMPATIBILITY_FRAME_START_MODE2_INTERRUPT_DELAY_CLOCKS = 4;
        private const int LYC_UPDATE_DELAY_CLOCKS = 4;
        private const int LCD_ENABLE_MODE_0_CLOCKS = 81;
        private const int CGB_DOUBLE_SPEED_LCD_ENABLE_MODE_0_CLOCKS = 80;
        private const int SEARCHING_SPRITES_ATTRIBUTES_CLOCKS = 80;
        private const int TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS = 172;
        // The first tile-number fetch latches fine SCX on every model; compatibility mode's extra dot delays output,
        // not this fetch phase.
        private const int SCX_FINE_SCROLL_LATCH_DOT = 8;
        private const int WINDOW_STARTUP_CLOCKS = 6;

        private const int MAX_SCROLL_AMOUNT = 256;

        private const int WINDOW_X_OFFSET = 7;

        private const int TILE_SIZE = 16;
        private const byte PALETTE_INDEX_UNUSED_READ_MASK = 0x40;

        private static readonly byte[] DefaultCompatibilityBackgroundPalette =
        {
            0xFF, 0x7F, 0xEF, 0x1B, 0x80, 0x61, 0x00, 0x00
        };

        private static readonly byte[] DefaultCompatibilityObjectPalettes =
        {
            0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00,
            0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00
        };

        private static readonly byte[] DefaultCompatibilityTrademarkTile =
        {
            0x3C, 0x00, 0x42, 0x00, 0xB9, 0x00, 0xA5, 0x00,
            0xB9, 0x00, 0xA5, 0x00, 0x42, 0x00, 0x3C, 0x00
        };

        private readonly Color[,] _screenData = new Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];
        private readonly Color[,] _renderData = new Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];

        private readonly byte[] _videoRAM = new byte[MemorySchema.MAX_VRAM_SIZE];
        private readonly byte[] _spriteAttributeTable = new byte[MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];
        private readonly byte[] _lineSpriteXCoordinates = new byte[10];
        private readonly byte[] _lineSpriteOamIndices = new byte[10];
        private readonly int[] _lineSpriteInitialFetchWaits = new int[10];
        private readonly int[] _lineSpriteSlotsByOamIndex = new int[40];
        private readonly byte[] _lineSpriteDataLow = new byte[10];
        private readonly byte[] _lineSpriteDataHigh = new byte[10];
        private readonly bool[] _scanlineObjectOutputEnabled = new bool[Display.HORIZONTAL_RESOLUTION];
        private readonly byte[] _scanlineScrollX = new byte[Display.HORIZONTAL_RESOLUTION];
        private readonly byte[] _scanlineScrollY = new byte[Display.HORIZONTAL_RESOLUTION];
        private readonly byte[] _gpuRegisters = new byte[MemorySchema.GPU_REGISTERS_END - MemorySchema.GPU_REGISTERS_START];

        private byte _bgPaletteIndex;
        private readonly byte[] _bgPaletteData;

        private byte _spritePaletteIndex;
        private readonly byte[] _spritePaletteData;

        private int _vRAMBank;

        private int _cycleCounter;
        private int _hBlankClockTarget = HBLANK_CLOCKS;
        private int _mode2StartDelayClockTarget = MODE2_START_DELAY_CLOCKS;
        private int _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
        private int _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION;
        private int _mode3RenderedPixels;
        private int _mode3LatchedObjectPixels;
        private int _mode3PreparedBackgroundPixels;
        private int _mode3WindowStartPixel = Display.HORIZONTAL_RESOLUTION;
        private int _mode3WindowRestartPixel = -1;
        private int _mode3WindowFetchPenalty = WINDOW_STARTUP_CLOCKS;
        private byte _windowLine;
        private byte _scanlineWindowLine;
        private int _mode3FetchTimelineDot;
        private int _mode3BackgroundFetcherDot;
        private int _mode3FetchPosition;
        private int _mode3NextSpriteIndex;
        private int _mode3ActiveSpriteIndex = -1;
        private int _mode3ObjectFetchWait;
        private int _mode3ObjectFetchStall;
        private int _mode3ObjectOutputStallDots;
        private int _lineSpriteCount;
        private byte _scanlineScrollXLow;
        private bool _objectOutputEnabled;
        private bool _windowRenderedThisScanline;
        private bool _line153EarlyReset;
        private bool _pendingVBlankInterrupt;
        private bool _statInterruptLineHigh;
        private bool _dmgVBlankMode2InterruptSource;
        private bool _dmgFrameStartMode2InterruptSource;
        private bool _coincidenceClearPending;
        private bool _coincidenceUpdatePending;
        private bool _lcdEnableStartup;
        private bool _mode2StartPending;
        private bool _hblankDmaWindowOpened;
        private bool _compatibilityFrameStartMode2InterruptPending;

        private bool _gbcMode = false;
        private bool _dmgCompatibilityMode;

        private readonly MessageBus _messageBus;

        /// <summary>
        /// Creates a PPU connected to the interrupt and HBlank bus for its owning emulator.
        /// </summary>
        public GPU(MessageBus messageBus)
        {
            _messageBus = messageBus;
            _messageBus.OnCanStartHBlankDmaImmediately = CanStartHBlankDmaImmediately;
            _messageBus.OnWriteOamDmaByte = WriteOamDmaByte;
            _bgPaletteData = Enumerable.Repeat<byte>(0xFF, MathSchema.MAX_6_BIT_VALUE).ToArray();
            _spritePaletteData = Enumerable.Repeat<byte>(0xFF, MathSchema.MAX_6_BIT_VALUE).ToArray();
        }

        /// <summary>
        /// Resets mode-dependent PPU state for a new emulator run.
        /// </summary>
        public void Reset(bool gbcMode)
        {
            Reset(gbcMode ? GBCMode.GBCSupport : GBCMode.NoGBC, usingBootROM: false);
        }

        /// <summary>
        /// Resets mode-dependent PPU state while preserving native CGB behavior during a color boot ROM.
        /// </summary>
        public void Reset(GBCMode mode, bool usingBootROM)
        {
            _gbcMode = mode != GBCMode.NoGBC && (mode != GBCMode.GBCCompatibility || usingBootROM);
            _dmgCompatibilityMode = mode == GBCMode.GBCCompatibility && !usingBootROM;
            for (var index = 0; index < MathSchema.MAX_6_BIT_VALUE; index++)
            {
                _bgPaletteData[index] = byte.MaxValue;
                _spritePaletteData[index] = byte.MaxValue;
            }

            if (_dmgCompatibilityMode)
            {
                InstallDefaultCompatibilityHandoff();
            }

            _statInterruptLineHigh = false;
            _dmgVBlankMode2InterruptSource = false;
            _dmgFrameStartMode2InterruptSource = false;
            _coincidenceClearPending = false;
            _coincidenceUpdatePending = false;
            _lcdEnableStartup = false;
            _mode2StartPending = false;
            _hblankDmaWindowOpened = false;
            _compatibilityFrameStartMode2InterruptPending = false;
            _cycleCounter = 0;
            _hBlankClockTarget = HBLANK_CLOCKS;
            _mode2StartDelayClockTarget = MODE2_START_DELAY_CLOCKS;
            _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
            _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION;
            _mode3RenderedPixels = 0;
            _mode3LatchedObjectPixels = 0;
            _mode3PreparedBackgroundPixels = 0;
            _mode3WindowStartPixel = Display.HORIZONTAL_RESOLUTION;
            _mode3WindowRestartPixel = -1;
            _mode3WindowFetchPenalty = WINDOW_STARTUP_CLOCKS;
            _windowLine = 0;
            _scanlineWindowLine = 0;
            _mode3FetchTimelineDot = 0;
            _mode3BackgroundFetcherDot = 0;
            _mode3FetchPosition = 0;
            _mode3NextSpriteIndex = 0;
            _mode3ActiveSpriteIndex = -1;
            _mode3ObjectFetchWait = 0;
            _mode3ObjectFetchStall = 0;
            _mode3ObjectOutputStallDots = 0;
            _lineSpriteCount = 0;
            _scanlineScrollXLow = 0;
            _objectOutputEnabled = false;
            _windowRenderedThisScanline = false;
            _line153EarlyReset = false;
            _gpuRegisters[(int)Registers.LCDStatus] = 0x85;
            BlankDisplay();
        }

        /// <summary>
        /// Installs the stock palette and retained trademark tile from the default CGB compatibility handoff.
        /// </summary>
        private void InstallDefaultCompatibilityHandoff()
        {
            Array.Copy(
                DefaultCompatibilityBackgroundPalette,
                _bgPaletteData,
                DefaultCompatibilityBackgroundPalette.Length);
            Array.Copy(
                DefaultCompatibilityObjectPalettes,
                _spritePaletteData,
                DefaultCompatibilityObjectPalettes.Length);
            Array.Copy(
                DefaultCompatibilityTrademarkTile,
                0,
                _videoRAM,
                0x19 * TILE_SIZE,
                DefaultCompatibilityTrademarkTile.Length);
        }

        /// <summary>
        /// Switches the PPU from boot-time CGB behavior to the DMG-compatible renderer at firmware handoff.
        /// </summary>
        public void EnterDmgCompatibilityMode()
        {
            _gbcMode = false;
            _dmgCompatibilityMode = true;
        }

        /// <summary>
        /// Advances PPU mode timing, scanline state, rendering, and LCD interrupt conditions by CPU-derived clocks.
        /// </summary>
        public void Update(int cycles)
        {
            if (!IsLCDEnabled())
            {
                _cycleCounter = 0;
                _gpuRegisters[(int)Registers.Scanline] = 0;
                SetStatusRegister(LCDStatus.HBlank);

                return;
            }

            _cycleCounter += cycles;

            while (IsLCDEnabled() && ProcessCurrentMode())
            {
            }
        }

        public bool CanReadWriteByte(int address)
        {
            if (address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END)
            {
                return true;
            }

            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START && address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                return true;
            }

            if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END && address != MemorySchema.DMA_REGISTER)
            {
                return true;
            }

            if (address == MemorySchema.GPU_VRAM_BANK_REGISTER)
            {
                return true;
            }

            if (address >= MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER && address <= MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads CPU-visible VRAM, OAM, or LCD register state with active PPU access restrictions applied.
        /// </summary>
        public byte ReadByte(int address)
        {
            if (address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END &&
                !IsCpuVramReadable())
            {
                return 0xFF;
            }

            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START && address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                if (!IsCpuOamAccessible())
                {
                    return 0xFF;
                }

                return _spriteAttributeTable[address - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];
            }

            if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END)
            {
                var register = address - MemorySchema.GPU_REGISTERS_START;
                var value = _gpuRegisters[register];

                // STAT bit 7 is unused on DMG hardware and is pulled high when read.
                return register == (int)Registers.LCDStatus
                    ? (byte)(value | 0x80)
                    : value;
            }

            if (address == MemorySchema.GPU_VRAM_BANK_REGISTER)
            {
                return (byte)_vRAMBank;
            }

            switch (address)
            {
                case MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER:
                    return (byte)(_bgPaletteIndex | PALETTE_INDEX_UNUSED_READ_MASK);

                case MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER:
                    return IsColorPaletteAccessible()
                        ? _bgPaletteData[Helpers.GetBits(_bgPaletteIndex, 6)]
                        : (byte)0xFF;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER:
                    return (byte)(_spritePaletteIndex | PALETTE_INDEX_UNUSED_READ_MASK);

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER:
                    return IsColorPaletteAccessible()
                        ? _spritePaletteData[Helpers.GetBits(_spritePaletteIndex, 6)]
                        : (byte)0xFF;
            }

            return ReadFromVRAMWithBank(address, _vRAMBank);
        }

        /// <summary>
        /// Writes VRAM, OAM, or an LCD register and applies its hardware side effects.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START && address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                _spriteAttributeTable[address - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START] = data;
            }
            else if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END)
            {
                address -= MemorySchema.GPU_REGISTERS_START;

                switch (address)
                {
                    case (int)Registers.LCDControl:
                        WriteLcdControl(data);
                        break;
                    case (int)Registers.LCDStatus:
                        // Mode and coincidence bits are read-only; UpdateCoincidenceFlag is authoritative for bit 2.
                        _gpuRegisters[address] = (byte)((_gpuRegisters[address] & 0x07) | (data & 0x78));
                        UpdateStatInterruptLine();
                        break;
                    case (int)Registers.ScrollX:
                        WriteScrollX(data);
                        break;
                    case (int)Registers.LCDYCoord:
                        _gpuRegisters[address] = data;
                        if (IsLCDEnabled())
                        {
                            UpdateCoincidenceFlag();
                            UpdateStatInterruptLine();
                        }
                        break;
                    case (int)Registers.WindowX:
                        WriteWindowX(data);
                        break;
                    default:
                        _gpuRegisters[address] = data;
                        break;
                }
            }
            else if (address == MemorySchema.GPU_VRAM_BANK_REGISTER)
            {
                _vRAMBank = Helpers.GetBits(data, 1);
            }
            else if (address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END)
            {
                _videoRAM[(address - MemorySchema.VIDEO_RAM_START) + (MemorySchema.MAX_VRAM_BANK_SIZE * _vRAMBank)] = data;
            }

            switch (address)
            {
                case MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER:
                    _bgPaletteIndex = data;
                    break;

                case MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER:
                    if (IsColorPaletteAccessible())
                    {
                        _bgPaletteData[Helpers.GetBits(_bgPaletteIndex, 6)] = data;
                    }

                    if (Helpers.TestBit(_bgPaletteIndex, 7))
                    {
                        _bgPaletteIndex = (byte)(0x80 | ((_bgPaletteIndex + 1) & 0x3F));
                    }

                    break;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER:
                    _spritePaletteIndex = data;
                    break;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER:
                    if (IsColorPaletteAccessible())
                    {
                        _spritePaletteData[Helpers.GetBits(_spritePaletteIndex, 6)] = data;
                    }

                    if (Helpers.TestBit(_spritePaletteIndex, 7))
                    {
                        _spritePaletteIndex = (byte)(0x80 | ((_spritePaletteIndex + 1) & 0x3F));
                    }

                    break;
            }
        }

        /// <summary>
        /// Writes CPU-visible VRAM or OAM only while the PPU has released the selected memory bus.
        /// </summary>
        internal void WriteByteForCpu(byte data, int address)
        {
            var mode = GetStatusMode();
            if (IsLCDEnabled() &&
                (mode == LCDStatus.TransferringDataToLCDDriver ||
                 address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                 address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END &&
                 !IsCpuOamWritable()))
            {
                return;
            }

            WriteByte(data, address);
        }

        /// <summary>
        /// Returns whether the CPU owns OAM at the current PPU phase, including the pre-mode-2 acquisition window.
        /// </summary>
        private bool IsCpuOamAccessible()
        {
            if (!IsLCDEnabled())
            {
                return true;
            }

            var mode = GetStatusMode();
            if (mode == LCDStatus.SearchingSpritesAttributes || mode == LCDStatus.TransferringDataToLCDDriver)
            {
                return false;
            }

            return !_mode2StartPending || _cycleCounter < LYC_UPDATE_DELAY_CLOCKS;
        }

        /// <summary>
        /// Returns whether a CPU OAM write reaches memory, including the short write window before mode 3.
        /// </summary>
        private bool IsCpuOamWritable()
        {
            if (!IsLCDEnabled())
            {
                return true;
            }

            var mode = GetStatusMode();
            if (mode == LCDStatus.TransferringDataToLCDDriver)
            {
                return false;
            }

            return mode != LCDStatus.SearchingSpritesAttributes ||
                   _cycleCounter >= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS - InstructionSchema.FOUR_CYCLES;
        }

        /// <summary>
        /// Returns whether a CPU VRAM read completes before the PPU acquires the fetch bus ahead of mode 3.
        /// </summary>
        private bool IsCpuVramReadable()
        {
            if (!IsLCDEnabled())
            {
                return true;
            }

            var mode = GetStatusMode();
            if (mode == LCDStatus.TransferringDataToLCDDriver)
            {
                return false;
            }

            return mode != LCDStatus.SearchingSpritesAttributes ||
                   _cycleCounter < SEARCHING_SPRITES_ATTRIBUTES_CLOCKS - InstructionSchema.FOUR_CYCLES;
        }

        /// <summary>
        /// Writes OAM through the DMA-owned port, which remains available while CPU and PPU OAM access is blocked.
        /// </summary>
        private void WriteOamDmaByte(byte data, int address)
        {
            _spriteAttributeTable[address - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START] = data;
        }

        /// <summary>
        /// Reads VRAM through the DMA-owned source port without applying CPU mode-3 restrictions.
        /// </summary>
        internal byte ReadOamDmaSourceByte(int address)
        {
            return ReadFromVRAMWithBank(address, _vRAMBank);
        }

        internal PpuDebugState GetDebugState()
        {
            return new PpuDebugState(
                (byte)ScanLine,
                _gpuRegisters[(int)Registers.LCDControl],
                _gpuRegisters[(int)Registers.LCDStatus],
                _cycleCounter);
        }

        public Color[,] GetScreenData()
        {
            return _screenData;
        }

        /// <summary>
        /// Applies LCD enable transitions that reset LY while pausing or restarting the LY=LYC comparison clock.
        /// </summary>
        private void WriteLcdControl(byte data)
        {
            var wasEnabled = IsLCDEnabled();
            _gpuRegisters[(int)Registers.LCDControl] = data;
            var isEnabled = IsLCDEnabled();

            if (wasEnabled == isEnabled)
            {
                return;
            }

            if (!isEnabled)
            {
                // Reset LY and mode while retaining the frozen coincidence source as STAT edge history.
                _cycleCounter = 0;
                _gpuRegisters[(int)Registers.Scanline] = 0;
                _windowLine = 0;
                _scanlineWindowLine = 0;
                _windowRenderedThisScanline = false;
                _line153EarlyReset = false;
                _coincidenceClearPending = false;
                _coincidenceUpdatePending = false;
                _lcdEnableStartup = false;
                _mode2StartPending = false;
                _dmgFrameStartMode2InterruptSource = false;
                _compatibilityFrameStartMode2InterruptPending = false;
                _hBlankClockTarget = HBLANK_CLOCKS;
                _mode2StartDelayClockTarget = MODE2_START_DELAY_CLOCKS;
                _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
                _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION;
                BlankDisplay();
                SetStatusRegister(LCDStatus.HBlank);
                return;
            }

            // DMG-family hardware begins line 0 in mode 0, skips mode 2, and exposes mode 3 after
            // its dedicated startup phase. Mooneye lcdon_timing-GS measures this transition.
            _cycleCounter = 0;
            _gpuRegisters[(int)Registers.Scanline] = 0;
            _windowLine = 0;
            _scanlineWindowLine = 0;
            _windowRenderedThisScanline = false;
            _coincidenceClearPending = false;
            _coincidenceUpdatePending = false;
            _lcdEnableStartup = true;
            _mode2StartPending = false;
            _dmgFrameStartMode2InterruptSource = false;
            _compatibilityFrameStartMode2InterruptPending = false;
            _hBlankClockTarget = HBLANK_CLOCKS;
            _mode2StartDelayClockTarget = MODE2_START_DELAY_CLOCKS;
            SetStatusRegister(LCDStatus.HBlank);
            UpdateCoincidenceFlag();
            UpdateStatInterruptLine();
        }

        /// <summary>
        /// Applies SCX immediately and retargets mode-3 startup until the first background tile-number fetch latches
        /// the low three bits for this scanline.
        /// </summary>
        private void WriteScrollX(byte data)
        {
            if (!IsLCDEnabled() ||
                GetStatusMode() != LCDStatus.TransferringDataToLCDDriver ||
                _cycleCounter > SCX_FINE_SCROLL_LATCH_DOT ||
                _mode3RenderedPixels != 0)
            {
                _gpuRegisters[(int)Registers.ScrollX] = data;
                return;
            }

            var oldFineScroll = _scanlineScrollXLow;
            var oldSpriteFetchPenalty = CalculateSpriteFetchPenalty();
            var oldWindowFetchPenalty = _mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION
                ? _mode3WindowFetchPenalty
                : 0;

            _gpuRegisters[(int)Registers.ScrollX] = data;
            _scanlineScrollXLow = (byte)(data & 0x07);

            var newSpriteFetchPenalty = CalculateSpriteFetchPenalty();
            _mode3WindowFetchPenalty = GetWindowFetchPenalty(_gpuRegisters[(int)Registers.WindowX]);
            var newWindowFetchPenalty = _mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION
                ? _mode3WindowFetchPenalty
                : 0;
            var timingDelta =
                _scanlineScrollXLow - oldFineScroll +
                newSpriteFetchPenalty - oldSpriteFetchPenalty +
                newWindowFetchPenalty - oldWindowFetchPenalty;

            _mode3StartupDots += _scanlineScrollXLow - oldFineScroll;
            _mode3ClockTarget += timingDelta;
            _hBlankClockTarget -= timingDelta;
        }

        /// <summary>
        /// Applies a live WX write to a pending window trigger, or records the color-zero pixel produced when an
        /// already-started window is moved forward to a position the LCD has not reached yet.
        /// </summary>
        private void WriteWindowX(byte data)
        {
            if (IsLCDEnabled() && GetStatusMode() == LCDStatus.TransferringDataToLCDDriver)
            {
                if (_mode3WindowRestartPixel >= _mode3RenderedPixels)
                {
                    _mode3WindowRestartPixel = -1;
                }

                var outputDots = Math.Max(0, _cycleCounter - _mode3StartupDots);
                var windowStarted =
                    _mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION &&
                    outputDots >= _mode3WindowStartPixel;
                var newStartPixel = GetWindowStartPixel(data);

                if (!windowStarted)
                {
                    var oldPenalty = _mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION
                        ? _mode3WindowFetchPenalty
                        : 0;
                    _mode3WindowFetchPenalty = GetWindowFetchPenalty(data);
                    var newPenalty = newStartPixel < Display.HORIZONTAL_RESOLUTION
                        ? _mode3WindowFetchPenalty
                        : 0;
                    var penaltyDelta = newPenalty - oldPenalty;
                    _mode3ClockTarget += penaltyDelta;
                    _hBlankClockTarget -= penaltyDelta;
                    _mode3WindowStartPixel = newStartPixel;
                }
                else if (newStartPixel >= _mode3RenderedPixels &&
                         newStartPixel < Display.HORIZONTAL_RESOLUTION &&
                         // A restart produces the transient color-zero pixel only on the tile-map fetch phase.
                         (newStartPixel & 0x07) == 5)
                {
                    _mode3WindowRestartPixel = newStartPixel;
                }
            }

            _gpuRegisters[(int)Registers.WindowX] = data;
        }

        /// <summary>
        /// Updates the read-only STAT coincidence flag after LY or LYC changes while its comparison clock runs.
        /// </summary>
        private void UpdateCoincidenceFlag()
        {
            var coincidence = ScanLine == _gpuRegisters[(int)Registers.LCDYCoord];
            Helpers.SetBit(ref _gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.Coincidence, coincidence);
        }

        /// <summary>
        /// Tracks the shared STAT source line and requests an LCD interrupt on an enabled low-to-high transition.
        /// </summary>
        private void UpdateStatInterruptLine()
        {
            var lcdEnabled = IsLCDEnabled();
            var status = _gpuRegisters[(int)Registers.LCDStatus];
            var mode = GetStatusMode();
            var modeSourceActive = lcdEnabled &&
                (mode == LCDStatus.HBlank && !IsFrameStartMode2Prelude() &&
                    IsInterruptEnabled(LCDStatusBits.HBlankInterruptEnabled) ||
                 mode == LCDStatus.VBlank && IsInterruptEnabled(LCDStatusBits.VBlankInterruptEnabled) ||
                 ((mode == LCDStatus.SearchingSpritesAttributes &&
                    !_compatibilityFrameStartMode2InterruptPending) ||
                   _dmgVBlankMode2InterruptSource ||
                   _dmgFrameStartMode2InterruptSource) &&
                    IsInterruptEnabled(LCDStatusBits.SearchingSpriteAttributesInterruptEnabled));
            var coincidenceSourceActive =
                Helpers.TestBit(status, (int)LCDStatusBits.Coincidence) &&
                IsInterruptEnabled(LCDStatusBits.CoincidenceInterruptEnabled);
            var interruptLineHigh = modeSourceActive || coincidenceSourceActive;

            // LCD-off freezes coincidence and its edge history, but the stopped comparison clock cannot request IRQs.
            if (lcdEnabled && interruptLineHigh && !_statInterruptLineHigh)
            {
                _messageBus.RequestInterrupt(Interrupts.LCD);
            }

            _statInterruptLineHigh = interruptLineHigh;
        }

        /// <summary>
        /// Processes every PPU event currently reachable with the accumulated dot count.
        /// </summary>
        private bool ProcessCurrentMode()
        {
            switch (GetStatusMode())
            {
                case LCDStatus.HBlank:
                    return HandleHBlank();
                case LCDStatus.VBlank:
                    return HandleVBlank();
                case LCDStatus.SearchingSpritesAttributes:
                    return HandleSearchingSpritesAttributes();
                case LCDStatus.TransferringDataToLCDDriver:
                    return TransferringDataToLCDDriver();
                default:
                    return false;
            }
        }

        /// <summary>
        /// Advances the LCD-enable prelude, visible-line HBlank, and the LY-to-mode-2 transition.
        /// </summary>
        private bool HandleHBlank()
        {
            if (_lcdEnableStartup)
            {
                var startupClockTarget = _gbcMode && _messageBus.GetCpuSpeedFactor() == 2
                    ? CGB_DOUBLE_SPEED_LCD_ENABLE_MODE_0_CLOCKS
                    : LCD_ENABLE_MODE_0_CLOCKS;
                if (_cycleCounter < startupClockTarget)
                {
                    return false;
                }

                _cycleCounter -= startupClockTarget;
                _lcdEnableStartup = false;
                PrepareMode3();
                return true;
            }

            if (_mode2StartPending)
            {
                if (_coincidenceClearPending && _cycleCounter >= LYC_UPDATE_DELAY_CLOCKS)
                {
                    _coincidenceClearPending = false;
                    Helpers.SetBit(ref _gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.Coincidence, false);
                    UpdateStatInterruptLine();
                    return true;
                }

                if (_cycleCounter < _mode2StartDelayClockTarget)
                {
                    return false;
                }

                var frameStartMode2 = IsFrameStartMode2Prelude();
                _cycleCounter -= _mode2StartDelayClockTarget;
                _mode2StartPending = false;
                _mode2StartDelayClockTarget = MODE2_START_DELAY_CLOCKS;
                _dmgFrameStartMode2InterruptSource = false;
                _compatibilityFrameStartMode2InterruptPending = frameStartMode2 && _dmgCompatibilityMode;
                if (_coincidenceUpdatePending)
                {
                    _coincidenceUpdatePending = false;
                    UpdateCoincidenceFlag();
                }

                SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
                return true;
            }

            if (_cycleCounter < _hBlankClockTarget)
            {
                return false;
            }

            _cycleCounter -= _hBlankClockTarget;
            var nextScanLine = ScanLine + 1;
            if (nextScanLine == Display.VERTICAL_RESOLUTION)
            {
                ScanLine = nextScanLine;
                _windowLine = 0;
                PublishFrame();
                _pendingVBlankInterrupt = true;
                SetStatusRegister(LCDStatus.VBlank);
            }
            else
            {
                _gpuRegisters[(int)Registers.Scanline] = (byte)nextScanLine;
                _coincidenceClearPending = true;
                _coincidenceUpdatePending = true;
                // LY advances after 196 HBlank dots, then mode 0 remains visible for eight more dots so
                // consecutive mode-2 starts stay 456 dots apart.
                _mode2StartDelayClockTarget = MODE2_START_DELAY_CLOCKS;
                _mode2StartPending = true;
                SetStatusRegister(LCDStatus.HBlank);
            }

            return true;
        }

        private bool HandleVBlank()
        {
            if (_pendingVBlankInterrupt && _cycleCounter >= 4)
            {
                _pendingVBlankInterrupt = false;
                _messageBus.VBlankStarted();
                _messageBus.RequestInterrupt(Interrupts.VBlank);

                // DMG hardware pulses the mode-2 STAT source with the VBlank request at line 144.
                _dmgVBlankMode2InterruptSource = IsDmgHardware();
                UpdateStatInterruptLine();
                _dmgVBlankMode2InterruptSource = false;
                UpdateStatInterruptLine();
                return true;
            }

            // LY exposes 153 only for the first four dots of its scanline, then reads as 0 while VBlank timing
            // continues. The early LY=0 comparison gives line-0 raster handlers almost a full scanline of lead time.
            if (!_line153EarlyReset && ScanLine == 153 && _cycleCounter >= 4)
            {
                _line153EarlyReset = true;
                ScanLine = 0;
                UpdateStatInterruptLine();
                return true;
            }

            if (_cycleCounter < SCANLINE_DRAW_CLOCKS)
            {
                return false;
            }

            _cycleCounter -= SCANLINE_DRAW_CLOCKS;
            if (_line153EarlyReset)
            {
                _line153EarlyReset = false;
                ScanLine = 0;
                _mode2StartDelayClockTarget = FRAME_START_MODE2_DELAY_CLOCKS;
                _mode2StartPending = true;
                _dmgFrameStartMode2InterruptSource = IsDmgHardware();
                SetStatusRegister(LCDStatus.HBlank);
            }
            else
            {
                ScanLine++;
                UpdateStatInterruptLine();
            }

            return true;
        }

        /// <summary>
        /// Completes the 80-dot OAM search and prepares pixel-transfer timing for the selected scanline sprites.
        /// </summary>
        private bool HandleSearchingSpritesAttributes()
        {
            if (_compatibilityFrameStartMode2InterruptPending &&
                _cycleCounter >= CGB_COMPATIBILITY_FRAME_START_MODE2_INTERRUPT_DELAY_CLOCKS)
            {
                _compatibilityFrameStartMode2InterruptPending = false;
                UpdateStatInterruptLine();
                return true;
            }

            if (_cycleCounter < SEARCHING_SPRITES_ATTRIBUTES_CLOCKS)
            {
                return false;
            }

            _cycleCounter -= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS;
            PrepareMode3();
            return true;
        }

        /// <summary>
        /// Captures mode-3 timing inputs and enters pixel transfer for the current visible scanline.
        /// </summary>
        private void PrepareMode3()
        {
            _scanlineScrollXLow = (byte)(_gpuRegisters[(int)Registers.ScrollX] & 0x07);

            var fineScrollPenalty = _scanlineScrollXLow;
            var spriteFetchPenalty = CalculateSpriteFetchPenalty();
            _mode3WindowStartPixel = GetWindowStartPixel();
            _mode3WindowFetchPenalty = GetWindowFetchPenalty(_gpuRegisters[(int)Registers.WindowX]);
            var windowFetchPenalty = _mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION
                ? _mode3WindowFetchPenalty
                : 0;
            // CGB compatibility output begins one dot later than the native CGB/DMG fetch phase measured here.
            _mode3StartupDots =
                TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION +
                fineScrollPenalty +
                (_dmgCompatibilityMode ? 1 : 0);
            _mode3ClockTarget =
                TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS +
                fineScrollPenalty +
                spriteFetchPenalty +
                windowFetchPenalty;
            // Line 143 retains four additional dots before entering VBlank; the ordinary eight-dot
            // LY-to-mode-2 prelude does not follow that transition.
            var baseHBlankClocks = ScanLine == Display.VERTICAL_RESOLUTION - 1
                ? VBLANK_ENTRY_HBLANK_CLOCKS
                : HBLANK_CLOCKS;
            _hBlankClockTarget = baseHBlankClocks - fineScrollPenalty - spriteFetchPenalty - windowFetchPenalty;
            _mode3RenderedPixels = 0;
            _mode3LatchedObjectPixels = 0;
            _mode3PreparedBackgroundPixels = 0;
            _scanlineWindowLine = _windowLine;
            _windowRenderedThisScanline = false;
            _mode3FetchTimelineDot = 0;
            _mode3BackgroundFetcherDot = 0;
            _mode3FetchPosition = -12;
            _mode3NextSpriteIndex = 0;
            _mode3ActiveSpriteIndex = -1;
            _mode3ObjectFetchWait = 0;
            _mode3ObjectFetchStall = 0;
            _mode3ObjectOutputStallDots = 0;
            _mode3WindowRestartPixel = -1;
            _objectOutputEnabled = Helpers.TestBit(
                _gpuRegisters[(int)Registers.LCDControl],
                (int)LCDControlBits.SpriteDisplayEnabled);
            _hblankDmaWindowOpened = false;
            SetStatusRegister(LCDStatus.TransferringDataToLCDDriver);
        }

        /// <summary>
        /// Completes the current pixel-transfer period, commits its scanline, and then starts HBlank side effects.
        /// </summary>
        private bool TransferringDataToLCDDriver()
        {
            RenderTransferredPixels();

            if (!_hblankDmaWindowOpened &&
                _cycleCounter >= _mode3ClockTarget - GetHBlankDmaLeadClocks())
            {
                _hblankDmaWindowOpened = true;
                _messageBus.HBlankDmaWindowOpened();
            }

            if (_cycleCounter < _mode3ClockTarget)
            {
                return false;
            }

            CompleteTransferredScanline();
            if (_windowRenderedThisScanline)
            {
                _windowLine++;
            }

            _cycleCounter -= _mode3ClockTarget;

            // The CPU-visible mode and its STAT source change together. Mooneye hblank_ly_scx_timing-GS
            // measures the shortened HBlank interval before LY changes, not a delayed mode-0 interrupt.
            SetStatusRegister(LCDStatus.HBlank);

            if (ScanLine < Display.VERTICAL_RESOLUTION)
            {
                // HDMA updates VRAM for subsequent scanlines; it must not alter the line that just completed.
                _messageBus.HBlankStarted();
            }

            return true;
        }

        /// <summary>
        /// Calculates the mode-3 stalls caused by the first ten OAM entries intersecting the current scanline.
        /// Each fetched sprite costs six dots, while sprites in one fetcher-aligned group share the initial wait
        /// described by Pan Docs. Sprites at X 168 or later consume an OAM slot but are not fetched.
        /// </summary>
        private int CalculateSpriteFetchPenalty()
        {
            _lineSpriteCount = 0;
            for (var index = 0; index < _lineSpriteSlotsByOamIndex.Length; index++)
            {
                _lineSpriteSlotsByOamIndex[index] = -1;
            }

            var control = _gpuRegisters[(int)Registers.LCDControl];
            if ((!_gbcMode && !Helpers.TestBit(control, (int)LCDControlBits.SpriteDisplayEnabled)) ||
                ScanLine >= Display.VERTICAL_RESOLUTION)
            {
                return 0;
            }

            var spriteHeight = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize) ? 16 : 8;
            var scrollX = GetEffectiveScrollX();

            for (var offset = 0; offset < _spriteAttributeTable.Length && _lineSpriteCount < 10; offset += 4)
            {
                var spriteY = _spriteAttributeTable[offset] - 16;
                if (ScanLine < spriteY || ScanLine >= spriteY + spriteHeight)
                {
                    continue;
                }

                _lineSpriteXCoordinates[_lineSpriteCount] = _spriteAttributeTable[offset + 1];
                _lineSpriteOamIndices[_lineSpriteCount] = (byte)(offset / 4);
                _lineSpriteCount++;
            }

            // Mode 2 selects in OAM order, but mode 3 fetches the selected sprites from left to right.
            for (var index = 1; index < _lineSpriteCount; index++)
            {
                var spriteX = _lineSpriteXCoordinates[index];
                var oamIndex = _lineSpriteOamIndices[index];
                var insertionIndex = index;
                while (insertionIndex > 0 && _lineSpriteXCoordinates[insertionIndex - 1] > spriteX)
                {
                    _lineSpriteXCoordinates[insertionIndex] = _lineSpriteXCoordinates[insertionIndex - 1];
                    _lineSpriteOamIndices[insertionIndex] = _lineSpriteOamIndices[insertionIndex - 1];
                    insertionIndex--;
                }

                _lineSpriteXCoordinates[insertionIndex] = spriteX;
                _lineSpriteOamIndices[insertionIndex] = oamIndex;
            }

            var penalty = 0;
            var previousFetchGroup = int.MinValue;
            for (var index = 0; index < _lineSpriteCount; index++)
            {
                var spriteX = _lineSpriteXCoordinates[index];
                if (spriteX >= Display.HORIZONTAL_RESOLUTION + 8)
                {
                    _lineSpriteCount = index;
                    break;
                }

                var fetchPosition = spriteX + scrollX;
                // OAM X=0 always pays the full initial fetch wait, regardless of background scroll alignment.
                var distanceFromGroupStart = spriteX == 0 ? 0 : fetchPosition & 0x07;
                var fetchGroup = fetchPosition & ~0x07;
                var initialFetchWait = fetchGroup != previousFetchGroup && distanceFromGroupStart < 5
                    ? 5 - distanceFromGroupStart
                    : 0;
                _lineSpriteInitialFetchWaits[index] = initialFetchWait;
                // The complete object fetch is its phase-alignment wait plus the six-dot VRAM transaction.
                penalty += initialFetchWait + 6;
                previousFetchGroup = fetchGroup;
            }

            for (var index = 0; index < _lineSpriteCount; index++)
            {
                _lineSpriteSlotsByOamIndex[_lineSpriteOamIndices[index]] = index;
            }

            // The PPU currently advances in CPU-supplied machine-cycle batches. Preserve the hardware-derived
            // dot cost, but expose only complete four-dot groups so mode transitions occur on the same boundary.
            return penalty & ~0x03;
        }

        private bool IsLCDEnabled()
        {
            return Helpers.TestBit(_gpuRegisters[(int)Registers.LCDControl], (int)LCDControlBits.LCDDisplayEnabled);
        }

        /// <summary>
        /// Returns whether the PPU is running on physical DMG hardware rather than CGB hardware's DMG renderer.
        /// </summary>
        private bool IsDmgHardware()
        {
            return !_gbcMode && !_dmgCompatibilityMode;
        }

        /// <summary>
        /// Reports whether a newly requested HBlank DMA block can begin without waiting for another mode transition.
        /// </summary>
        private bool CanStartHBlankDmaImmediately()
        {
            return !IsLCDEnabled() ||
                   GetStatusMode() == LCDStatus.HBlank && !IsFrameStartMode2Prelude();
        }

        /// <summary>
        /// Returns whether STAT temporarily reads mode 0 between VBlank and line-zero mode 2 without a real HBlank.
        /// </summary>
        private bool IsFrameStartMode2Prelude()
        {
            return _mode2StartPending && _mode2StartDelayClockTarget == FRAME_START_MODE2_DELAY_CLOCKS;
        }

        /// <summary>
        /// Returns the lead time for the CGB HBlank DMA bus request. The request is observed on a CPU M-cycle
        /// boundary; fine scroll values of two or more move acquisition to the later measured boundary.
        /// </summary>
        private int GetHBlankDmaLeadClocks()
        {
            var leadClocks = InstructionSchema.FOUR_CYCLES / _messageBus.GetCpuSpeedFactor();
            return _scanlineScrollXLow > 1 ? Math.Max(1, leadClocks / 2) : leadClocks;
        }

        /// <summary>
        /// Combines the live tile-column scroll with the low three bits latched before mode 3.
        /// </summary>
        private byte GetEffectiveScrollX()
        {
            return (byte)((_gpuRegisters[(int)Registers.ScrollX] & 0xF8) | _scanlineScrollXLow);
        }

        /// <summary>
        /// Returns the first visible pixel reached by a window enabled at mode-3 entry, or the display width when
        /// no window fetch will occur on this scanline.
        /// </summary>
        private int GetWindowStartPixel()
        {
            return GetWindowStartPixel(_gpuRegisters[(int)Registers.WindowX]);
        }

        /// <summary>
        /// Returns the visible trigger position for a supplied WX value under the current scanline's window state.
        /// </summary>
        private int GetWindowStartPixel(byte windowX)
        {
            var control = _gpuRegisters[(int)Registers.LCDControl];
            var bgWindowEnabled = _gbcMode || Helpers.TestBit(control, (int)LCDControlBits.BGDisplayEnabled);
            if (!bgWindowEnabled ||
                !Helpers.TestBit(control, (int)LCDControlBits.WindowDisplayEnabled) ||
                _gpuRegisters[(int)Registers.WindowY] > ScanLine)
            {
                return Display.HORIZONTAL_RESOLUTION;
            }

            return Math.Min(
                Display.HORIZONTAL_RESOLUTION,
                Math.Max(0, windowX - WINDOW_X_OFFSET));
        }

        /// <summary>
        /// Returns the window fetch stall, including the one-dot activation delay measured by Mealybug on CGB-C when
        /// WX zero switches the fetcher while fine scroll is nonzero.
        /// </summary>
        private int GetWindowFetchPenalty(byte windowX)
        {
            return !IsDmgHardware() && windowX == 0 && _scanlineScrollXLow != 0
                ? WINDOW_STARTUP_CLOCKS + 1
                : WINDOW_STARTUP_CLOCKS;
        }

        /// <summary>
        /// Reports whether CPU-visible CGB palette RAM can be accessed in the current PPU mode.
        /// </summary>
        private bool IsColorPaletteAccessible()
        {
            return !_gbcMode || !IsLCDEnabled() || GetStatusMode() != LCDStatus.TransferringDataToLCDDriver;
        }

        /// <summary>
        /// Advances background tile-number fetches and latches live scroll values at each GetTile T1 address phase.
        /// Object fetches first align the background fetcher, then hold it for their six-dot VRAM transaction.
        /// </summary>
        private void AdvanceBackgroundFetchTimeline()
        {
            while (_mode3FetchTimelineDot < _cycleCounter)
            {
                _mode3FetchTimelineDot++;

                if (_mode3ObjectFetchWait == 0 &&
                    _mode3ObjectFetchStall == 0 &&
                    _mode3NextSpriteIndex < _lineSpriteCount &&
                    IsObjectFetchMatch(_lineSpriteXCoordinates[_mode3NextSpriteIndex]))
                {
                    var spriteIndex = _mode3NextSpriteIndex++;
                    _mode3ActiveSpriteIndex = spriteIndex;
                    var spriteX = _lineSpriteXCoordinates[spriteIndex];
                    var initialFetchWait = _lineSpriteInitialFetchWaits[spriteIndex];
                    // OAM X 0-8 matches while the fetch stream is still at or before the visible left edge. Mealybug's
                    // aligned-sprite SCX tests show that its phase wait delays the next T1 sample instead of advancing it.
                    _mode3ObjectFetchWait = spriteX <= 8 ? 0 : initialFetchWait;
                    _mode3ObjectFetchStall = spriteX <= 8 ? initialFetchWait + 6 : 6;
                }

                if (_mode3ObjectFetchWait > 0)
                {
                    _mode3ObjectFetchWait--;
                    _mode3ObjectOutputStallDots++;
                    AdvanceBackgroundFetcherDot();
                    AdvanceObjectOutputLatch();
                    continue;
                }

                if (_mode3ObjectFetchStall > 0)
                {
                    var transactionDot = _mode3ObjectFetchStall <= 6
                        ? 7 - _mode3ObjectFetchStall
                        : 0;
                    if (transactionDot == 3)
                    {
                        CaptureObjectTileByte(_mode3ActiveSpriteIndex, highByte: false);
                    }
                    else if (transactionDot == 5)
                    {
                        CaptureObjectTileByte(_mode3ActiveSpriteIndex, highByte: true);
                    }

                    _mode3ObjectFetchStall--;
                    _mode3ObjectOutputStallDots++;
                    if (_mode3ObjectFetchStall == 0)
                    {
                        _mode3ActiveSpriteIndex = -1;
                    }
                    AdvanceObjectOutputLatch();
                    continue;
                }

                _mode3FetchPosition++;
                AdvanceBackgroundFetcherDot();
                AdvanceObjectOutputLatch();
            }
        }

        /// <summary>
        /// Matches OAM X against the fetch stream, including its startup-to-visible boundary.
        /// </summary>
        private bool IsObjectFetchMatch(int spriteX)
        {
            return spriteX + _scanlineScrollXLow == _mode3FetchPosition + 8;
        }

        /// <summary>
        /// Captures one object tile-data byte using the live LCDC.2 size at that VRAM address phase.
        /// </summary>
        private void CaptureObjectTileByte(int spriteIndex, bool highByte)
        {
            var oamOffset = _lineSpriteOamIndices[spriteIndex] * 4;
            var spriteY = _spriteAttributeTable[oamOffset] - 16;
            var tileIndex = _spriteAttributeTable[oamOffset + 2];
            var attributes = _spriteAttributeTable[oamOffset + 3];
            var use8x16 = Helpers.TestBit(_gpuRegisters[(int)Registers.LCDControl], (int)LCDControlBits.SpriteSize);
            var spriteHeight = use8x16 ? 16 : 8;
            var tilePixelRow = (ScanLine - spriteY) & (spriteHeight - 1);
            if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.YFlip))
            {
                tilePixelRow ^= spriteHeight - 1;
            }

            if (use8x16)
            {
                tileIndex &= 0xFE;
            }

            var bank = _gbcMode ? Helpers.GetBit(attributes, (int)SpriteAttributesBits.TileVRAMBankNumber) : 0;
            var tileAddress =
                MemorySchema.TILE_DATA_UNSIGNED_START +
                tileIndex * TILE_SIZE +
                tilePixelRow * 2 +
                (highByte ? 1 : 0);
            var data = ReadFromVRAMWithBank(tileAddress, bank);
            if (highByte)
            {
                _lineSpriteDataHigh[spriteIndex] = data;
            }
            else
            {
                _lineSpriteDataLow[spriteIndex] = data;
            }
        }

        /// <summary>
        /// Advances one active background-fetcher dot and records a complete tile slice when its address is sampled.
        /// </summary>
        private void AdvanceBackgroundFetcherDot()
        {
            _mode3BackgroundFetcherDot++;
            if (_mode3BackgroundFetcherDot < 9 || (_mode3BackgroundFetcherDot - 9) % 8 != 0)
            {
                return;
            }

            var scrollX = GetEffectiveScrollX();
            var scrollY = _gpuRegisters[(int)Registers.ScrollY];
            var fetchedTileEnd = Math.Min(
                Display.HORIZONTAL_RESOLUTION,
                _mode3PreparedBackgroundPixels + 8);
            while (_mode3PreparedBackgroundPixels < fetchedTileEnd)
            {
                _scanlineScrollX[_mode3PreparedBackgroundPixels] = scrollX;
                _scanlineScrollY[_mode3PreparedBackgroundPixels] = scrollY;
                _mode3PreparedBackgroundPixels++;
            }
        }

        /// <summary>
        /// Samples LCDC.1 on every transfer dot and records its retained value whenever one pixel leaves the FIFO.
        /// CGB object selection and fetch therefore continue independently while output is disabled.
        /// </summary>
        private void AdvanceObjectOutputLatch()
        {
            var outputDots = Math.Max(
                0,
                _mode3FetchTimelineDot - _mode3StartupDots - _mode3ObjectOutputStallDots);
            var completedPixels = outputDots;
            if (_mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION && outputDots > _mode3WindowStartPixel)
            {
                completedPixels =
                    _mode3WindowStartPixel +
                    Math.Max(0, outputDots - _mode3WindowStartPixel - _mode3WindowFetchPenalty);
            }

            completedPixels = Math.Min(
                Math.Min(Display.HORIZONTAL_RESOLUTION, completedPixels),
                _mode3PreparedBackgroundPixels);
            while (_mode3LatchedObjectPixels < completedPixels)
            {
                _scanlineObjectOutputEnabled[_mode3LatchedObjectPixels++] = _objectOutputEnabled;
            }

            _objectOutputEnabled = Helpers.TestBit(
                _gpuRegisters[(int)Registers.LCDControl],
                (int)LCDControlBits.SpriteDisplayEnabled);
        }

        /// <summary>
        /// Commits pixels whose transfer dots have elapsed using the register and palette state visible now.
        /// </summary>
        private void RenderTransferredPixels()
        {
            AdvanceBackgroundFetchTimeline();

            // Preserve every measured object-fetch pause in the LCD output timeline. The mode-transition target is
            // separately quantized to complete four-dot CPU groups; completion commits its remaining right-edge pixels.
            var outputDots = Math.Max(
                0,
                _cycleCounter - _mode3StartupDots - _mode3ObjectOutputStallDots);
            var completedPixels = outputDots;
            if (_mode3WindowStartPixel < Display.HORIZONTAL_RESOLUTION && outputDots > _mode3WindowStartPixel)
            {
                completedPixels =
                    _mode3WindowStartPixel +
                    Math.Max(0, outputDots - _mode3WindowStartPixel - _mode3WindowFetchPenalty);
            }

            completedPixels = Math.Min(
                Math.Min(Display.HORIZONTAL_RESOLUTION, completedPixels),
                _mode3PreparedBackgroundPixels);

            if (completedPixels <= _mode3RenderedPixels)
            {
                return;
            }

            DrawScanLine(_mode3RenderedPixels, completedPixels);
            _mode3RenderedPixels = completedPixels;
        }

        /// <summary>
        /// Commits any right-edge pixels still pending when the transfer clock target is reached. Mode 3 completion
        /// always delivers all 160 visible pixels even when raster writes retarget startup timing mid-fetch.
        /// </summary>
        private void CompleteTransferredScanline()
        {
            if (_mode3RenderedPixels >= Display.HORIZONTAL_RESOLUTION)
            {
                return;
            }

            while (_mode3PreparedBackgroundPixels < Display.HORIZONTAL_RESOLUTION)
            {
                AdvanceBackgroundFetcherDot();
            }
            while (_mode3LatchedObjectPixels < Display.HORIZONTAL_RESOLUTION)
            {
                _scanlineObjectOutputEnabled[_mode3LatchedObjectPixels++] = _objectOutputEnabled;
                _objectOutputEnabled = Helpers.TestBit(
                    _gpuRegisters[(int)Registers.LCDControl],
                    (int)LCDControlBits.SpriteDisplayEnabled);
            }
            DrawScanLine(_mode3RenderedPixels, Display.HORIZONTAL_RESOLUTION);
            _mode3RenderedPixels = Display.HORIZONTAL_RESOLUTION;
        }

        /// <summary>
        /// Blanks both display buffers when the LCD is disabled so hosts cannot retain pixels from the previous frame.
        /// </summary>
        private void BlankDisplay()
        {
            var color = _gbcMode ? new Color(byte.MaxValue, byte.MaxValue, byte.MaxValue) : new Color(Display.DefaultPalette[0]);
            for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
            {
                for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
                {
                    _screenData[x, y] = color;
                    _renderData[x, y] = color;
                }
            }
        }

        /// <summary>
        /// Copies the completed render frame into the stable host-visible framebuffer at VBlank entry.
        /// </summary>
        private void PublishFrame()
        {
            for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
            {
                for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
                {
                    _screenData[x, y] = _renderData[x, y];
                }
            }
        }

        /// <summary>
        /// Composes one visible scanline using DMG or CGB LCDC background-priority semantics.
        /// </summary>
        private void DrawScanLine(int startPixel, int endPixel)
        {
            var control = _gpuRegisters[(int)Registers.LCDControl];
            var bgWindowEnabled = _gbcMode || Helpers.TestBit(control, (int)LCDControlBits.BGDisplayEnabled);

            if (bgWindowEnabled)
            {
                RenderBackground(control, startPixel, endPixel);
            }
            else
            {
                ClearBackgroundScanLine(startPixel, endPixel);
            }

            if (bgWindowEnabled && Helpers.TestBit(control, (int)LCDControlBits.WindowDisplayEnabled))
            {
                RenderWindow(control, startPixel, endPixel);
            }

            if (_lineSpriteCount > 0)
            {
                RenderSprites(control, startPixel, endPixel);
            }
        }

        /// <summary>
        /// Writes DMG color zero across a scanline when LCDC.0 disables both the background and window.
        /// </summary>
        private void ClearBackgroundScanLine(int startPixel, int endPixel)
        {
            var color = GetColor(true, 0, 0, (int)Registers.BackgroundTilePalette);
            for (var pixel = startPixel; pixel < endPixel; pixel++)
            {
                _renderData[pixel, ScanLine] = color;
            }
        }

        private void RenderBackground(byte control, int startPixel, int endPixel)
        {
            var rangeStart = startPixel;
            while (rangeStart < endPixel)
            {
                var scrollX = _scanlineScrollX[rangeStart];
                var scrollY = _scanlineScrollY[rangeStart];
                var rangeEnd = rangeStart + 1;
                while (rangeEnd < endPixel &&
                       _scanlineScrollX[rangeEnd] == scrollX &&
                       _scanlineScrollY[rangeEnd] == scrollY)
                {
                    rangeEnd++;
                }

                RenderTiles(control, scrollX, (scrollY + ScanLine) % MAX_SCROLL_AMOUNT, rangeStart, rangeEnd);
                rangeStart = rangeEnd;
            }
        }

        /// <summary>
        /// Renders the window from its internal line coordinate, which advances only on scanlines that reach a
        /// visible window trigger.
        /// </summary>
        private void RenderWindow(byte control, int startPixel, int endPixel)
        {
            var windowX = _gpuRegisters[(int)Registers.WindowX] - WINDOW_X_OFFSET;
            var windowY = _gpuRegisters[(int)Registers.WindowY];

            if (windowY <= ScanLine)
            {
                if (endPixel > Math.Max(startPixel, Math.Max(0, windowX)))
                {
                    _windowRenderedThisScanline = true;
                }

                RenderTiles(
                    control,
                    windowX,
                    _scanlineWindowLine,
                    startPixel,
                    endPixel,
                    true);

                if (_mode3WindowRestartPixel >= startPixel && _mode3WindowRestartPixel < endPixel)
                {
                    _renderData[_mode3WindowRestartPixel, ScanLine] =
                        GetColor(true, 0, 0, (int)Registers.BackgroundTilePalette);
                }
            }
        }

        private void RenderTiles(
            byte control,
            int xPos,
            int yPos,
            int startPixel,
            int endPixel,
            bool window = false)
        {
            var tileDataLoc = Helpers.TestBit(control, (int)LCDControlBits.BGWindowTileDataSelect) ? MemorySchema.TILE_DATA_UNSIGNED_START : MemorySchema.TILE_DATA_SIGNED_START;
            var backgroundMemoryLoc = Helpers.TestBit(control, window ? (int)LCDControlBits.WindowTileMapSelect : (int)LCDControlBits.BGTileMapSelect) ? MemorySchema.BACKGROUND_LAYOUT_1_START : MemorySchema.BACKGROUND_LAYOUT_0_START;

            var signed = tileDataLoc == MemorySchema.TILE_DATA_SIGNED_START;

            //TODO get rid of below magic numbers
            var tileRow = ((byte)(yPos / 8)) * 32;
            var offset = signed ? 128 : 0;

            for (var pixel = startPixel; pixel < endPixel; pixel++)
            {
                var x = pixel + xPos;

                if (window)
                {
                    if (pixel >= xPos)
                    {
                        x = pixel - xPos;
                    }
                }
                else
                {
                    x %= MAX_SCROLL_AMOUNT;
                }

                var tileCol = x / 8;
                var tileMemIndex = backgroundMemoryLoc + tileRow + tileCol;

                var attributes = GetBGAttributes(tileMemIndex);
                // CGB tile IDs always come from bank 0; VBK selects only the CPU-visible bank.
                var data = ReadFromVRAMWithBank(tileMemIndex, 0);
                var tileNum = signed ? (int)(sbyte)data : data;

                var tileLoc = tileDataLoc + ((tileNum + offset) * TILE_SIZE);

                var line = (yPos % 8) * 2;
                if (Helpers.TestBit(attributes, (int)BGAttributeBits.YFlip))
                {
                    line = 14 - line;
                }

                var bank = Helpers.GetBit(attributes, (int)BGAttributeBits.TileVRAMBankNumber);

                var data1 = ReadFromVRAMWithBank(tileLoc + line, bank);
                var data2 = ReadFromVRAMWithBank(tileLoc + line + 1, bank);

                x = x % 8;
                if (Helpers.TestBit(attributes, (int)BGAttributeBits.XFlip))
                {
                    x = 7 - x;
                }

                var colorBit = (x - 7) * -1;

                var colorNum = (Helpers.GetBit(data2, colorBit) << 1) | Helpers.GetBit(data1, colorBit);

                if (window && pixel < xPos)
                {
                    continue;
                }

                var color = GetColor(true, (byte)colorNum, attributes, (int)Registers.BackgroundTilePalette);
                color.BGPriority = Helpers.TestBit(attributes, (int)BGAttributeBits.BGToSpritePriority);
                _renderData[pixel, ScanLine] = color;
            }
        }

        /// <summary>
        /// Resolves the winning mode-2-selected object pixel before comparing that winner with background priority.
        /// </summary>
        private void RenderSprites(byte control, int startPixel, int endPixel)
        {
            if (ScanLine >= Display.VERTICAL_RESOLUTION)
            {
                return;
            }

            for (var pixel = startPixel; pixel < endPixel; pixel++)
            {
                if (!_scanlineObjectOutputEnabled[pixel] ||
                    !TryGetWinningObjectPixel(pixel, out var spriteIndex, out var colorValue))
                {
                    continue;
                }

                var oamOffset = _lineSpriteOamIndices[spriteIndex] * 4;
                var attributes = _spriteAttributeTable[oamOffset + 3];
                var background = _renderData[pixel, ScanLine];
                var objectsAlwaysOnTop =
                    _gbcMode && !Helpers.TestBit(control, (int)LCDControlBits.BGDisplayEnabled);
                if (!objectsAlwaysOnTop &&
                    background.Index != 0 &&
                    (Helpers.TestBit(attributes, (int)SpriteAttributesBits.SpriteToBGPriority) ||
                     background.BGPriority))
                {
                    continue;
                }

                var paletteAddress = Helpers.TestBit(attributes, (int)SpriteAttributesBits.PaletteNum)
                    ? (int)Registers.SpritePalette1
                    : (int)Registers.SpritePalette0;
                _renderData[pixel, ScanLine] = GetColor(false, colorValue, attributes, paletteAddress);
            }
        }

        /// <summary>
        /// Finds the first opaque object pixel in the active hardware's drawing-priority order.
        /// </summary>
        private bool TryGetWinningObjectPixel(int pixel, out int winningSpriteIndex, out byte colorValue)
        {
            if (_gbcMode)
            {
                for (var oamIndex = 0; oamIndex < _lineSpriteSlotsByOamIndex.Length; oamIndex++)
                {
                    var spriteIndex = _lineSpriteSlotsByOamIndex[oamIndex];
                    if (spriteIndex >= 0 && TryGetObjectColorValue(spriteIndex, pixel, out colorValue))
                    {
                        winningSpriteIndex = spriteIndex;
                        return true;
                    }
                }
            }
            else
            {
                // The selected list is stably sorted by X, preserving OAM order for equal coordinates.
                for (var spriteIndex = 0; spriteIndex < _lineSpriteCount; spriteIndex++)
                {
                    if (TryGetObjectColorValue(spriteIndex, pixel, out colorValue))
                    {
                        winningSpriteIndex = spriteIndex;
                        return true;
                    }
                }
            }

            winningSpriteIndex = -1;
            colorValue = 0;
            return false;
        }

        /// <summary>
        /// Returns one fetched object's opaque color index at a visible screen pixel.
        /// </summary>
        private bool TryGetObjectColorValue(int spriteIndex, int pixel, out byte colorValue)
        {
            var oamOffset = _lineSpriteOamIndices[spriteIndex] * 4;
            var objectX = _lineSpriteXCoordinates[spriteIndex] - 8;
            var tilePixelColumn = pixel - objectX;
            if (tilePixelColumn < 0 || tilePixelColumn >= 8)
            {
                colorValue = 0;
                return false;
            }

            var attributes = _spriteAttributeTable[oamOffset + 3];
            if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.XFlip))
            {
                tilePixelColumn = 7 - tilePixelColumn;
            }

            var data1 = _lineSpriteDataLow[spriteIndex];
            var data2 = _lineSpriteDataHigh[spriteIndex];
            colorValue = (byte)(
                ((data1 >> (7 - tilePixelColumn)) & 1) |
                (((data2 >> (7 - tilePixelColumn)) & 1) << 1));
            return colorValue != 0;
        }

        private void SetStatusRegister(LCDStatus status)
        {
            var bit0 = false;
            var bit1 = false;

            //Is there a more mathematically correct way of doing this?
            switch (status)
            {
                case LCDStatus.VBlank:
                    bit0 = true;
                    break;
                case LCDStatus.SearchingSpritesAttributes:
                    bit1 = true;
                    break;
                case LCDStatus.TransferringDataToLCDDriver:
                    bit0 = true;
                    bit1 = true;
                    break;
            }

            Helpers.SetBit(ref _gpuRegisters[(int)Registers.LCDStatus], 0, bit0);
            Helpers.SetBit(ref _gpuRegisters[(int)Registers.LCDStatus], 1, bit1);
            UpdateStatInterruptLine();
        }

        private LCDStatus GetStatusMode()
        {
            return (LCDStatus)Helpers.GetBits(_gpuRegisters[(int)Registers.LCDStatus], 2);
        }

        private int GetColorIndex(byte colorNum, int paletteAddress)
        {
            var palette = ReadByte(paletteAddress);

            int high;
            int low;

            switch (colorNum)
            {
                case 0: high = 1; low = 0; break;
                case 1: high = 3; low = 2; break;
                case 2: high = 5; low = 4; break;
                case 3: high = 7; low = 6; break;
                default: throw new IndexOutOfRangeException();
            }

            var color = (Helpers.GetBit(palette, high) << 1) | Helpers.GetBit(palette, low);

            if (color > 3)
            {
                throw new IndexOutOfRangeException();
            }

            return color;
        }

        private Color GetColor(bool bgWindow, byte colorValue, byte attributes, int paletteAddress)
        {
            var rawColorValue = colorValue;

            if (!_gbcMode && !_dmgCompatibilityMode)
            {
                var colorIndex = GetColorIndex(colorValue, MemorySchema.GPU_REGISTERS_START | paletteAddress);

                return new Color(Display.DefaultPalette[colorIndex])
                {
                    Index = colorValue,
                    SgbIndex = (byte)colorIndex
                }; //TODO replace with swappable colors
            }

            var paletteIndex = _dmgCompatibilityMode
                ? (bgWindow || !Helpers.TestBit(attributes, (int)SpriteAttributesBits.PaletteNum) ? 0 : 1)
                : Helpers.GetBits(attributes, 3);

            if (_dmgCompatibilityMode)
            {
                colorValue = (byte)GetColorIndex(colorValue, MemorySchema.GPU_REGISTERS_START | paletteAddress);
            }

            var palette = bgWindow ? _bgPaletteData : _spritePaletteData;

            var index = paletteIndex * 8 + colorValue * 2;
            var colorBytes = palette[index] | (palette[index + 1] << 8);

            return new Color(
                    r: ExpandFiveBit(colorBytes & 0x1F),
                    g: ExpandFiveBit((colorBytes >> 5) & 0x1F),
                    b: ExpandFiveBit((colorBytes >> 10) & 0x1F)
                )
            { Index = rawColorValue, SgbIndex = colorValue };
        }

        /// <summary>
        /// Expands a five-bit palette component across the full eight-bit output range.
        /// </summary>
        private static byte ExpandFiveBit(int value)
        {
            return (byte)((value << 3) | (value >> 2));
        }

        private bool IsInterruptEnabled(LCDStatusBits status)
        {
            return Helpers.TestBit(_gpuRegisters[(int)Registers.LCDStatus], (int)status);
        }

        private byte GetBGAttributes(int index)
        {
            return (byte)(_gbcMode ? _videoRAM[(MemorySchema.MAX_VRAM_BANK_SIZE + index) - MemorySchema.VIDEO_RAM_START] : 0);
        }

        private byte ReadFromVRAMWithBank(int address, int bank)
        {
            return _videoRAM[(address - MemorySchema.VIDEO_RAM_START) + MemorySchema.MAX_VRAM_BANK_SIZE * bank];
        }
    }
}
