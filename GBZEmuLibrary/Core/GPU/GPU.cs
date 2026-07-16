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

        private const int MAX_SCANLINES = 154; //153;
        private const int MAX_SCROLL_AMOUNT = 256;

        private const int WINDOW_X_OFFSET = 7;

        private const int TILE_SIZE = 16;

        private readonly Color[,] _screenData = new Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];

        private readonly byte[] _videoRAM = new byte[MemorySchema.MAX_VRAM_SIZE];
        private readonly byte[] _spriteAttributeTable = new byte[MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];
        private readonly byte[] _lineSpriteXCoordinates = new byte[10];
        private readonly byte[] _gpuRegisters = new byte[MemorySchema.GPU_REGISTERS_END - MemorySchema.GPU_REGISTERS_START];

        private byte _bgPaletteIndex;
        private readonly byte[] _bgPaletteData;

        private byte _spritePaletteIndex;
        private readonly byte[] _spritePaletteData;

        private int _vRAMBank;

        private int _cycleCounter;
        private int _hBlankClockTarget = HBLANK_CLOCKS;
        private int _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
        private bool _pendingVBlankInterrupt;
        private bool _statInterruptLineHigh;
        private bool _dmgVBlankMode2InterruptSource;

        private bool _gbcMode = false;

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
            _gbcMode = gbcMode;
            _statInterruptLineHigh = false;
            _dmgVBlankMode2InterruptSource = false;
            _hBlankClockTarget = HBLANK_CLOCKS;
            _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
            _gpuRegisters[(int)Registers.LCDStatus] = 0x85;
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

            switch (GetStatusMode())
            {
                case LCDStatus.HBlank:
                    HandleHBlank();
                    break;
                case LCDStatus.VBlank:
                    HandleVBlank();
                    break;
                case LCDStatus.SearchingSpritesAttributes:
                    HandleSearchingSpritesAttributes();
                    break;
                case LCDStatus.TransferringDataToLCDDriver:
                    TransferringDataToLCDDriver();
                    break;
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
                    return _bgPaletteData[Helpers.GetBits(_bgPaletteIndex, 6)];

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER:
                    return _spritePaletteIndex;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER:
                    return _spritePaletteData[Helpers.GetBits(_spritePaletteIndex, 6)];
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
                    _bgPaletteData[Helpers.GetBits(_bgPaletteIndex, 6)] = data;
                    if (Helpers.TestBit(_bgPaletteIndex, 7))
                    {
                        _bgPaletteIndex = (byte)(0x80 | ((_bgPaletteIndex + 1) & 0x3F));
                    }

                    break;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER:
                    _spritePaletteIndex = data;
                    break;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER:
                    _spritePaletteData[Helpers.GetBits(_spritePaletteIndex, 6)] = data;
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
                _hBlankClockTarget = HBLANK_CLOCKS;
                _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;
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

        private void HandleHBlank()
        {
            if (_cycleCounter >= _hBlankClockTarget)
            {
                _cycleCounter -= _hBlankClockTarget;

                ScanLine++;

                if (ScanLine == Display.VERTICAL_RESOLUTION)
                {
                    _pendingVBlankInterrupt = true;
                    SetStatusRegister(LCDStatus.VBlank);
                }
                else
                {
                    SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
                }
            }
        }

        private void HandleVBlank()
        {
            if (_cycleCounter >= SCANLINE_DRAW_CLOCKS)
            {
                _cycleCounter -= SCANLINE_DRAW_CLOCKS;

                ScanLine++;

                if (ScanLine > Display.VERTICAL_RESOLUTION + 9) //TODO figure out what the magic number is about?
                {
                    ScanLine = 0;
                    SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
                }
                else
                {
                    UpdateStatInterruptLine();
                }
            }
            else if (_pendingVBlankInterrupt && _cycleCounter >= 4)
            {
                _pendingVBlankInterrupt = false;
                _messageBus.RequestInterrupt(Interrupts.VBlank);

                // DMG hardware pulses the mode-2 STAT source with the VBlank request at line 144.
                _dmgVBlankMode2InterruptSource = !_gbcMode;
                UpdateStatInterruptLine();
                _dmgVBlankMode2InterruptSource = false;
                UpdateStatInterruptLine();
            }
        }

        private void HandleSearchingSpritesAttributes()
        {
            if (_cycleCounter >= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS)
            {
                _cycleCounter -= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS;

                var spriteFetchPenalty = CalculateSpriteFetchPenalty();
                _mode3ClockTarget = TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + spriteFetchPenalty;
                _hBlankClockTarget = HBLANK_CLOCKS - spriteFetchPenalty;
                SetStatusRegister(LCDStatus.TransferringDataToLCDDriver);
            }
        }

        private void TransferringDataToLCDDriver()
        {
            if (_cycleCounter >= _mode3ClockTarget)
            {
                _cycleCounter -= _mode3ClockTarget;

                SetStatusRegister(LCDStatus.HBlank);

                if (ScanLine < Display.VERTICAL_RESOLUTION)
                {
                    _messageBus.HBlankStarted();
                }

                DrawScanLine();
            }
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
            var scrollX = _gpuRegisters[(int)Registers.ScrollX];
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

        private void DrawScanLine()
        {
            var control = _gpuRegisters[(int)Registers.LCDControl];

            if (Helpers.TestBit(control, (int)LCDControlBits.BGDisplayEnabled))
            {
                RenderBackground(control);
            }

            if (Helpers.TestBit(control, (int)LCDControlBits.WindowDisplayEnabled))
            {
                RenderWindow(control);
            }

            if (Helpers.TestBit(control, (int)LCDControlBits.SpriteDisplayEnabled))
            {
                RenderSprites(control);
            }
        }

        private void RenderBackground(byte control)
        {
            var scrollX = _gpuRegisters[(int)Registers.ScrollX];
            var scrollY = _gpuRegisters[(int)Registers.ScrollY];

            RenderTiles(control, scrollX, (scrollY + ScanLine) % MAX_SCROLL_AMOUNT);
        }

        private void RenderWindow(byte control)
        {
            var windowX = _gpuRegisters[(int)Registers.WindowX] - WINDOW_X_OFFSET;
            var windowY = _gpuRegisters[(int)Registers.WindowY];

            if (windowY <= ScanLine)
            {
                RenderTiles(control, windowX, (ScanLine - windowY) % MAX_SCROLL_AMOUNT, true);
            }
        }

        private void RenderTiles(byte control, int xPos, int yPos, bool window = false)
        {
            var tileDataLoc = Helpers.TestBit(control, (int)LCDControlBits.BGWindowTileDataSelect) ? MemorySchema.TILE_DATA_UNSIGNED_START : MemorySchema.TILE_DATA_SIGNED_START;
            var backgroundMemoryLoc = Helpers.TestBit(control, window ? (int)LCDControlBits.WindowTileMapSelect : (int)LCDControlBits.BGTileMapSelect) ? MemorySchema.BACKGROUND_LAYOUT_1_START : MemorySchema.BACKGROUND_LAYOUT_0_START;

            var signed = tileDataLoc == MemorySchema.TILE_DATA_SIGNED_START;

            //TODO get rid of below magic numbers
            var tileRow = ((byte)(yPos / 8)) * 32;
            var offset = signed ? 128 : 0;

            for (var pixel = 0; pixel < Display.HORIZONTAL_RESOLUTION; pixel++)
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
                _screenData[pixel, ScanLine] = color;
            }
        }

        private void RenderSprites(byte control)
        {
            var use8x16 = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize);
            var ySize = use8x16 ? 16 : 8;

            const int tableStart = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START;

            for (var i = (Display.HORIZONTAL_RESOLUTION - 4); i >= 0; i -= 4)
            {
                var y = ReadByte(tableStart + i) - 16;
                var x = ReadByte(tableStart + i + 1) - 8;

                if (ScanLine >= y && ScanLine < (y + ySize))
                {
                    var tileIndex = ReadByte(tableStart + i + 2);
                    if (use8x16)
                    {
                        tileIndex &= 0xFE;
                    }

                    var attributes = ReadByte(tableStart + i + 3);
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

                        if (spriteX >= 0 && spriteX < Display.HORIZONTAL_RESOLUTION && ScanLine < Display.VERTICAL_RESOLUTION)
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

                            if (((Helpers.TestBit(attributes, (int)SpriteAttributesBits.SpriteToBGPriority) && _screenData[spriteX, ScanLine].Index != 0) || _screenData[spriteX, ScanLine].BGPriority) && !spritePriority)
                            {
                                continue;
                            }

                            _screenData[spriteX, ScanLine] = GetColor(false, colorValue, attributes, paletteAddress);
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
            if (!_gbcMode)
            {
                var colorIndex = GetColorIndex(colorValue, MemorySchema.GPU_REGISTERS_START | paletteAddress);

                return new Color(Display.DefaultPalette[colorIndex])
                {
                    Index = colorValue
                }; //TODO replace with swappable colors
            }

            var paletteIndex = Helpers.GetBits(attributes, 3);

            var palette = bgWindow ? _bgPaletteData : _spritePaletteData;

            var index = paletteIndex * 8 + colorValue * 2;
            var colorBytes = palette[index] | (palette[index + 1] << 8);

            return new Color(
                    r: (byte)((colorBytes & 0x1F) * 8),
                    g: (byte)(((colorBytes >> 5) & 0x1F) * 8),
                    b: (byte)(((colorBytes >> 10) & 0x1F) * 8)
                )
            { Index = colorValue };
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
