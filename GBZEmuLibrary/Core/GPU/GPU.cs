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
        private const int HBLANK_CLOCKS = 204;
        private const int SEARCHING_SPRITES_ATTRIBUTES_CLOCKS = 80;
        private const int TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS = 172;

        private const int MAX_SCROLL_AMOUNT = 256;

        private const int WINDOW_X_OFFSET = 7;

        private const int TILE_SIZE = 16;

        private readonly Color[,] _screenData = new Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];
        private readonly Color[,] _renderData = new Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];

        private readonly byte[] _videoRAM = new byte[MemorySchema.MAX_VRAM_SIZE];
        private readonly byte[] _spriteAttributeTable = new byte[MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];
        private readonly byte[] _lineSpriteXCoordinates = new byte[10];
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
        private int _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
        private int _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION;
        private int _mode3RenderedPixels;
        private int _mode3PreparedBackgroundPixels;
        private byte _scanlineScrollXLow;
        private bool _line153EarlyReset;
        private bool _pendingVBlankInterrupt;
        private bool _statInterruptLineHigh;
        private bool _dmgVBlankMode2InterruptSource;

        private bool _gbcMode = false;
        private bool _dmgCompatibilityMode;

        private readonly MessageBus _messageBus;

        /// <summary>
        /// Creates a PPU connected to the interrupt and HBlank bus for its owning emulator.
        /// </summary>
        public GPU(MessageBus messageBus)
        {
            _messageBus = messageBus;
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
            _statInterruptLineHigh = false;
            _dmgVBlankMode2InterruptSource = false;
            _hBlankClockTarget = HBLANK_CLOCKS;
            _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
            _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION;
            _mode3RenderedPixels = 0;
            _mode3PreparedBackgroundPixels = 0;
            _scanlineScrollXLow = 0;
            _line153EarlyReset = false;
            _gpuRegisters[(int)Registers.LCDStatus] = 0x85;
            BlankDisplay();
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

        public byte ReadByte(int address)
        {
            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START && address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                // CPU reads are blocked while the PPU scans OAM or transfers pixel data.
                var mode = GetStatusMode();
                if (IsLCDEnabled() &&
                    (mode == LCDStatus.SearchingSpritesAttributes || mode == LCDStatus.TransferringDataToLCDDriver))
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
                    return _bgPaletteIndex;

                case MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER:
                    return IsColorPaletteAccessible()
                        ? _bgPaletteData[Helpers.GetBits(_bgPaletteIndex, 6)]
                        : (byte)0xFF;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER:
                    return _spritePaletteIndex;

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
                    case (int)Registers.LCDYCoord:
                        _gpuRegisters[address] = data;
                        if (IsLCDEnabled())
                        {
                            UpdateCoincidenceFlag();
                            UpdateStatInterruptLine();
                        }
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
                _gpuRegisters[(int)Registers.Scanline] = 0;
                _line153EarlyReset = false;
                _hBlankClockTarget = HBLANK_CLOCKS;
                _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
                _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION;
                BlankDisplay();
                SetStatusRegister(LCDStatus.HBlank);
                return;
            }

            UpdateCoincidenceFlag();
            UpdateStatInterruptLine();
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
                (mode == LCDStatus.HBlank && IsInterruptEnabled(LCDStatusBits.HBlankInterruptEnabled) ||
                 mode == LCDStatus.VBlank && IsInterruptEnabled(LCDStatusBits.VBlankInterruptEnabled) ||
                 (mode == LCDStatus.SearchingSpritesAttributes || _dmgVBlankMode2InterruptSource) &&
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

        private bool HandleHBlank()
        {
            if (_cycleCounter < _hBlankClockTarget)
            {
                return false;
            }

            _cycleCounter -= _hBlankClockTarget;
            ScanLine++;

            if (ScanLine == Display.VERTICAL_RESOLUTION)
            {
                PublishFrame();
                _pendingVBlankInterrupt = true;
                SetStatusRegister(LCDStatus.VBlank);
            }
            else
            {
                SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
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
                _dmgVBlankMode2InterruptSource = !_gbcMode;
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
                SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
            }
            else
            {
                ScanLine++;
                UpdateStatInterruptLine();
            }

            return true;
        }

        private bool HandleSearchingSpritesAttributes()
        {
            if (_cycleCounter < SEARCHING_SPRITES_ATTRIBUTES_CLOCKS)
            {
                return false;
            }

            _cycleCounter -= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS;
            _scanlineScrollXLow = (byte)(_gpuRegisters[(int)Registers.ScrollX] & 0x07);

            var fineScrollPenalty = _scanlineScrollXLow;
            var spriteFetchPenalty = CalculateSpriteFetchPenalty();
            _mode3StartupDots = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS - Display.HORIZONTAL_RESOLUTION + fineScrollPenalty;
            _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + fineScrollPenalty + spriteFetchPenalty;
            _hBlankClockTarget = HBLANK_CLOCKS - fineScrollPenalty - spriteFetchPenalty;
            _mode3RenderedPixels = 0;
            _mode3PreparedBackgroundPixels = 0;
            SetStatusRegister(LCDStatus.TransferringDataToLCDDriver);
            return true;
        }

        /// <summary>
        /// Completes the current pixel-transfer period, commits its scanline, and then starts HBlank side effects.
        /// </summary>
        private bool TransferringDataToLCDDriver()
        {
            RenderTransferredPixels();

            if (_cycleCounter < _mode3ClockTarget)
            {
                return false;
            }

            _cycleCounter -= _mode3ClockTarget;

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
            var control = _gpuRegisters[(int)Registers.LCDControl];
            if ((!_gbcMode && !Helpers.TestBit(control, (int)LCDControlBits.SpriteDisplayEnabled)) ||
                ScanLine >= Display.VERTICAL_RESOLUTION)
            {
                return 0;
            }

            var spriteHeight = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize) ? 16 : 8;
            var scrollX = GetEffectiveScrollX();
            var selectedSpriteCount = 0;

            for (var offset = 0; offset < _spriteAttributeTable.Length && selectedSpriteCount < 10; offset += 4)
            {
                var spriteY = _spriteAttributeTable[offset] - 16;
                if (ScanLine < spriteY || ScanLine >= spriteY + spriteHeight)
                {
                    continue;
                }

                _lineSpriteXCoordinates[selectedSpriteCount++] = _spriteAttributeTable[offset + 1];
            }

            // Mode 2 selects in OAM order, but mode 3 fetches the selected sprites from left to right.
            for (var index = 1; index < selectedSpriteCount; index++)
            {
                var spriteX = _lineSpriteXCoordinates[index];
                var insertionIndex = index;
                while (insertionIndex > 0 && _lineSpriteXCoordinates[insertionIndex - 1] > spriteX)
                {
                    _lineSpriteXCoordinates[insertionIndex] = _lineSpriteXCoordinates[insertionIndex - 1];
                    insertionIndex--;
                }

                _lineSpriteXCoordinates[insertionIndex] = spriteX;
            }

            var penalty = 0;
            var previousFetchGroup = int.MinValue;
            for (var index = 0; index < selectedSpriteCount; index++)
            {
                var spriteX = _lineSpriteXCoordinates[index];
                if (spriteX >= Display.HORIZONTAL_RESOLUTION + 8)
                {
                    break;
                }

                var fetchPosition = spriteX + scrollX;
                // OAM X=0 always pays the full initial fetch wait, regardless of background scroll alignment.
                var distanceFromGroupStart = spriteX == 0 ? 0 : fetchPosition & 0x07;
                var fetchGroup = fetchPosition & ~0x07;
                penalty += fetchGroup != previousFetchGroup && distanceFromGroupStart < 5
                    ? 11 - distanceFromGroupStart
                    : 6;
                previousFetchGroup = fetchGroup;
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
        /// Combines the live tile-column scroll with the low three bits latched before mode 3.
        /// </summary>
        private byte GetEffectiveScrollX()
        {
            return (byte)((_gpuRegisters[(int)Registers.ScrollX] & 0xF8) | _scanlineScrollXLow);
        }

        /// <summary>
        /// Reports whether CPU-visible CGB palette RAM can be accessed in the current PPU mode.
        /// </summary>
        private bool IsColorPaletteAccessible()
        {
            return !_gbcMode || !IsLCDEnabled() || GetStatusMode() != LCDStatus.TransferringDataToLCDDriver;
        }

        /// <summary>
        /// Commits pixels whose transfer dots have elapsed using the register and palette state visible now.
        /// </summary>
        private void RenderTransferredPixels()
        {
            var completedPixels = Math.Min(
                Display.HORIZONTAL_RESOLUTION,
                Math.Max(0, _cycleCounter - _mode3StartupDots));
            PrepareBackgroundScroll(Math.Min(Display.HORIZONTAL_RESOLUTION, completedPixels + 16));

            if (completedPixels <= _mode3RenderedPixels)
            {
                return;
            }

            DrawScanLine(_mode3RenderedPixels, completedPixels);
            _mode3RenderedPixels = completedPixels;
        }

        /// <summary>
        /// Samples scroll registers for background pixels fetched up to 16 pixels ahead of LCD output.
        /// </summary>
        private void PrepareBackgroundScroll(int fetchedPixels)
        {
            var scrollX = GetEffectiveScrollX();
            var scrollY = _gpuRegisters[(int)Registers.ScrollY];
            while (_mode3PreparedBackgroundPixels < fetchedPixels)
            {
                _scanlineScrollX[_mode3PreparedBackgroundPixels] = scrollX;
                _scanlineScrollY[_mode3PreparedBackgroundPixels] = scrollY;
                _mode3PreparedBackgroundPixels++;
            }
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

            if (Helpers.TestBit(control, (int)LCDControlBits.SpriteDisplayEnabled))
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

        private void RenderWindow(byte control, int startPixel, int endPixel)
        {
            var windowX = _gpuRegisters[(int)Registers.WindowX] - WINDOW_X_OFFSET;
            var windowY = _gpuRegisters[(int)Registers.WindowY];

            if (windowY <= ScanLine)
            {
                RenderTiles(
                    control,
                    windowX,
                    (ScanLine - windowY) % MAX_SCROLL_AMOUNT,
                    startPixel,
                    endPixel,
                    true);
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

        private void RenderSprites(byte control, int startPixel, int endPixel)
        {
            var use8x16 = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize);
            var ySize = use8x16 ? 16 : 8;

            const int tableStart = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START;

            for (var i = (Display.HORIZONTAL_RESOLUTION - 4); i >= 0; i -= 4)
            {
                var oamOffset = tableStart + i - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START;
                var y = _spriteAttributeTable[oamOffset] - 16;
                var x = _spriteAttributeTable[oamOffset + 1] - 8;

                if (ScanLine >= y && ScanLine < (y + ySize))
                {
                    var tileIndex = _spriteAttributeTable[oamOffset + 2];
                    if (use8x16)
                    {
                        tileIndex &= 0xFE;
                    }

                    var attributes = _spriteAttributeTable[oamOffset + 3];
                    var bank = _gbcMode ? Helpers.GetBit(attributes, (int)SpriteAttributesBits.TileVRAMBankNumber) : 0;

                    var tilePixelRow = ScanLine - y;

                    if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.YFlip))
                    {
                        tilePixelRow = Math.Abs(tilePixelRow - (ySize - 1));
                    }

                    var tileLineOffset = tilePixelRow * 2;
                    var tileAddress = MemorySchema.TILE_DATA_UNSIGNED_START + (tileIndex * TILE_SIZE);

                    var data1 = ReadFromVRAMWithBank(tileAddress + tileLineOffset, bank);
                    var data2 = ReadFromVRAMWithBank(tileAddress + tileLineOffset + 1, bank);
                    var paletteAddress = Helpers.TestBit(attributes, (int)SpriteAttributesBits.PaletteNum) ? (int)Registers.SpritePalette1 : (int)Registers.SpritePalette0;

                    for (var column = 0; column < 8; column++)
                    {
                        var spriteX = x + column;

                        if (spriteX >= startPixel && spriteX < endPixel && ScanLine < Display.VERTICAL_RESOLUTION)
                        {
                            var tilePixelColumn = column;

                            if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.XFlip))
                            {
                                tilePixelColumn = Math.Abs(tilePixelColumn - 7);
                            }

                            byte colorValue = 0;
                            colorValue |= (byte)((data1 >> (7 - tilePixelColumn)) & 1);
                            colorValue |= (byte)(((data2 >> (7 - tilePixelColumn)) & 1) << 1);

                            if (colorValue == 0)
                            {
                                continue;
                            }

                            // Bit0 of LCD control register in GBC mode make sprites always render on top
                            var spritePriority = _gbcMode && !Helpers.TestBit(control, (int)LCDControlBits.BGDisplayEnabled);

                            if (((Helpers.TestBit(attributes, (int)SpriteAttributesBits.SpriteToBGPriority) && _renderData[spriteX, ScanLine].Index != 0) || _renderData[spriteX, ScanLine].BGPriority) && !spritePriority)
                            {
                                continue;
                            }

                            _renderData[spriteX, ScanLine] = GetColor(false, colorValue, attributes, paletteAddress);
                        }
                    }
                }
            }
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
