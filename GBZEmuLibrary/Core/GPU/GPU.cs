using System;
using System.Linq;

namespace GBZEmuLibrary
{
    internal class GPU
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

        private const int SCANLINE_DRAW_CLOCKS = 456; //TODO maybe use floats and get more accuracy as this should be more like 456.8 for 60FPS
        private const int HBLANK_CLOCKS       = 204;
        private const int SEARCHING_SPRITES_ATTRIBUTES_CLOCKS = 80;
        private const int TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS = 172;

        private const int MAX_SCANLINES     = 154; //153;
        private const int MAX_SCROLL_AMOUNT = 256;

        private const int WINDOW_X_OFFSET = 7;

        private const int TILE_SIZE = 16;

        private readonly Color[,] _screenData = new Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];

        private readonly byte[] _videoRAM             = new byte[MemorySchema.MAX_VRAM_SIZE];
        private readonly byte[] _spriteAttributeTable = new byte[MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];
        private readonly byte[] _gpuRegisters = new byte[MemorySchema.GPU_REGISTERS_END - MemorySchema.GPU_REGISTERS_START];

        private byte _bgPaletteIndex;
        private readonly byte[] _bgPaletteData;

        private byte _spritePaletteIndex;
        private readonly byte[] _spritePaletteData;

        private int _vRAMBank;

        private int  _cycleCounter;
        private bool _pendingVBlankInterrupt;

        private GBCMode _mode;

        public GPU()
        {
            _bgPaletteData = Enumerable.Repeat<byte>(0xFF, MathSchema.MAX_6_BIT_VALUE).ToArray();
            _spritePaletteData = Enumerable.Repeat<byte>(0xFF, MathSchema.MAX_6_BIT_VALUE).ToArray();
        }

        public void Init(GBCMode mode)
        {
            _mode = mode;
        }

        public void Update(int cycles)
        {
            if (!IsLCDEnabled())
            {
                _cycleCounter                          = 0;
                _gpuRegisters[(int)Registers.Scanline] = 0;
                SetStatusRegister(LCDStatus.HBlank);

                return;
            }

            _cycleCounter += cycles;

            var currentLine      = _gpuRegisters[(int)Registers.Scanline];
            var requestInterrupt = false;

            switch (GetStatusMode())
            {
                case LCDStatus.HBlank:
                    requestInterrupt = HandleHBlank(currentLine);
                    break;
                case LCDStatus.VBlank:
                    requestInterrupt = HandleVBlank(currentLine);
                    break;
                case LCDStatus.SearchingSpritesAttributes:
                    HandleSearchingSpritesAttributes();
                    break;
                case LCDStatus.TransferringDataToLCDDriver:
                    requestInterrupt = TransferringDataToLCDDriver();
                    break;
            }

            //Request interrupt if mode has changed or a coincidence has occurred
            if (requestInterrupt)
            {
                MessageBus.Instance.RequestInterrupt(Interrupts.LCD);
            }
        }

        public byte ReadByte(int address)
        {
            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START && address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                return _spriteAttributeTable[address - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];
            }

            if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END)
            {
                return _gpuRegisters[address - MemorySchema.GPU_REGISTERS_START];
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

        public void WriteByte(byte data, int address)
        {
            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START && address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                _spriteAttributeTable[address - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START] = data;
            }
            else if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END)
            {
                _gpuRegisters[address - MemorySchema.GPU_REGISTERS_START] = data;
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
                        _bgPaletteIndex++;
                    }

                    break;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER:
                    _spritePaletteIndex = data;
                    break;

                case MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER:
                    _spritePaletteData[Helpers.GetBits(_spritePaletteIndex, 6)] = data;
                    if (Helpers.TestBit(_spritePaletteIndex, 7))
                    {
                        _spritePaletteIndex++;
                    }
                    break;
            }
        }

        public Color[,] GetScreenData()
        {
            return _screenData;
        }

        private bool HandleHBlank(int currentLine)
        {
            var requestInterrupt = false;

            if (_cycleCounter >= HBLANK_CLOCKS)
            {
                _cycleCounter -= HBLANK_CLOCKS;

                requestInterrupt = UpdateScanline(ref currentLine);

                if (currentLine == Display.VERTICAL_RESOLUTION)
                {
                    _pendingVBlankInterrupt = true;
                    SetStatusRegister(LCDStatus.VBlank);

                    requestInterrupt |= IsInterruptEnabled(LCDStatusBits.HBlankInterruptEnabled);
                }
                else
                {
                    SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
                    requestInterrupt |= IsInterruptEnabled(LCDStatusBits.SearchingSpriteAttributesInterruptEnabled);
                }
            }

            return requestInterrupt;
        }

        private bool HandleVBlank(int currentLine)
        {
            var requestInterrupt = false;

            if (_cycleCounter >= SCANLINE_DRAW_CLOCKS)
            {
                _cycleCounter -= SCANLINE_DRAW_CLOCKS;

                requestInterrupt = UpdateScanline(ref currentLine);

                if (currentLine == 0) //Possibly need to deal with line 0 timing
                {
                    SetStatusRegister(LCDStatus.SearchingSpritesAttributes);
                    requestInterrupt |= IsInterruptEnabled(LCDStatusBits.SearchingSpriteAttributesInterruptEnabled);
                }
            }
            else if (_pendingVBlankInterrupt && _cycleCounter >= 4)
            {
                _pendingVBlankInterrupt = false;
                MessageBus.Instance.RequestInterrupt(Interrupts.VBlank);
            }

            return requestInterrupt;
        }

        private void HandleSearchingSpritesAttributes()
        {
            if (_cycleCounter >= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS)
            {
                _cycleCounter -= SEARCHING_SPRITES_ATTRIBUTES_CLOCKS;

                SetStatusRegister(LCDStatus.TransferringDataToLCDDriver);
            }
        }

        private bool TransferringDataToLCDDriver()
        {
            var requestInterrupt = false;

            if (_cycleCounter >= TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS)
            {
                _cycleCounter -= TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS;

                SetStatusRegister(LCDStatus.HBlank);
                requestInterrupt = IsInterruptEnabled(LCDStatusBits.HBlankInterruptEnabled);

                DrawScanLine();
            }

            return requestInterrupt;
        }

        private bool UpdateScanline(ref int currentLine)
        {
            currentLine = (currentLine + 1) % MAX_SCANLINES;
            _gpuRegisters[(int)Registers.Scanline] = (byte)currentLine;

            var coincidence = currentLine == _gpuRegisters[(int)Registers.LCDYCoord];
            Helpers.SetBit(ref _gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.Coincidence, coincidence);

            return coincidence;
        }

        private bool IsLCDEnabled()
        {
            return Helpers.TestBit(_gpuRegisters[(int)Registers.LCDControl], (int)LCDControlBits.LCDDisplayEnabled);
        }

        private void DrawScanLine()
        {
            var control  = _gpuRegisters[(int)Registers.LCDControl];
            var scanline = _gpuRegisters[(int)Registers.Scanline];

            if (Helpers.TestBit(control, (int)LCDControlBits.BGDisplayEnabled))
            {
                RenderBackground(control, scanline);
            }

            if (Helpers.TestBit(control, (int)LCDControlBits.WindowDisplayEnabled))
            {
                RenderWindow(control, scanline);
            }

            if (Helpers.TestBit(control, (int)LCDControlBits.SpriteDisplayEnabled))
            {
                RenderSprites(control, scanline);
            }
        }

        private void RenderBackground(byte control, byte scanline)
        {
            var scrollX = _gpuRegisters[(int)Registers.ScrollX];
            var scrollY = _gpuRegisters[(int)Registers.ScrollY];

            RenderTiles(control, scanline, scrollX, (scrollY + scanline) % MAX_SCROLL_AMOUNT);
        }

        private void RenderWindow(byte control, byte scanline)
        {
            var windowX = _gpuRegisters[(int)Registers.WindowX] - WINDOW_X_OFFSET;
            var windowY = _gpuRegisters[(int)Registers.WindowY];

            if (windowY <= scanline)
            {
                RenderTiles(control, scanline, windowX, (scanline - windowY) % MAX_SCROLL_AMOUNT, true);
            }
        }

        private void RenderTiles(byte control, byte scanline, int xPos, int yPos, bool window = false)
        {
            var tileDataLoc         = Helpers.TestBit(control, (int)LCDControlBits.BGWindowTileDataSelect) ? MemorySchema.TILE_DATA_UNSIGNED_START : MemorySchema.TILE_DATA_SIGNED_START;
            var backgroundMemoryLoc = Helpers.TestBit(control, window ? (int)LCDControlBits.WindowTileMapSelect : (int)LCDControlBits.BGTileMapSelect) ? MemorySchema.BACKGROUND_LAYOUT_1_START : MemorySchema.BACKGROUND_LAYOUT_0_START;

            var signed = tileDataLoc == MemorySchema.TILE_DATA_SIGNED_START;

            //TODO get rid of below magic numbers
            var tileRow = ((byte)(yPos / 8)) * 32;
            var offset  = signed ? 128 : 0;

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
                var data    = ReadByte(tileMemIndex);
                var tileNum = signed ? (int)(sbyte)data : data;

                var tileLoc = tileDataLoc + ((tileNum + offset) * TILE_SIZE);

                var line  = (yPos % 8) * 2;
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

                //TODO maybe need to implement gameboy pixel fetcher to sort out BG priorty over sprite
                _screenData[pixel, scanline] = GetColor(true, (byte)colorNum, attributes, (int)Registers.BackgroundTilePalette);
            }
        }

        private void RenderSprites(byte control, byte scanline)
        {
            var use8x16 = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize);
            var ySize   = use8x16 ? 16 : 8;

            const int tableStart = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START;

            for (var i = (Display.HORIZONTAL_RESOLUTION - 4); i >= 0; i -= 4)
            {
                var y = ReadByte(tableStart + i) - 16;
                var x = ReadByte(tableStart + i + 1) - 8;

                if (scanline >= y && scanline < (y + ySize))
                {
                    var tileIndex = ReadByte(tableStart + i + 2);
                    if (use8x16)
                    {
                        tileIndex &= 0xFE;
                    }

                    var attributes = ReadByte(tableStart + i + 3);

                    var tilePixelRow = scanline - y;

                    if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.YFlip))
                    {
                        tilePixelRow = Math.Abs(tilePixelRow - (ySize - 1));
                    }

                    var tileLineOffset = tilePixelRow * 2;
                    var tileAddress = MemorySchema.TILE_DATA_UNSIGNED_START + (tileIndex * TILE_SIZE);

                    var bank = _mode != GBCMode.NoGBC ? Helpers.GetBit(attributes, (int)BGAttributeBits.TileVRAMBankNumber) : 0;

                    var data1 = ReadFromVRAMWithBank(tileAddress + tileLineOffset, bank);
                    var data2 = ReadFromVRAMWithBank(tileAddress + tileLineOffset + 1, bank);
                    var paletteAddress = Helpers.TestBit(attributes, (int)SpriteAttributesBits.PaletteNum) ? (int)Registers.SpritePalette1 : (int)Registers.SpritePalette0;

                    for (var column = 0; column < 8; column++)
                    {
                        var spriteX = x + column;

                        if (spriteX >= 0 && spriteX < Display.HORIZONTAL_RESOLUTION && scanline < Display.VERTICAL_RESOLUTION)
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

                            if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.SpriteToBGPriority) && _screenData[spriteX, scanline].Index != 0)
                            {
                                continue;
                            }

                            _screenData[spriteX, scanline] = GetColor(false, colorValue, attributes, paletteAddress);
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
            if (_mode == GBCMode.NoGBC)
            {

                var colorIndex = GetColorIndex(colorValue, MemorySchema.GPU_REGISTERS_START | paletteAddress);

                return new Color(Display.DefaultPalette[colorIndex])
                {
                    Index = colorValue
                }; //TODO replace with swappable colors
            }

            var paletteIndex = Helpers.GetBits(attributes, 3);

            var palette = bgWindow ? _bgPaletteData : _spritePaletteData;

            var index      = paletteIndex * 8 + colorValue * 2;
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
            return (byte)(_mode != GBCMode.NoGBC ? _videoRAM[(MemorySchema.MAX_VRAM_BANK_SIZE + index) - MemorySchema.VIDEO_RAM_START] : 0);
        }

        private byte ReadFromVRAMWithBank(int address, int bank)
        {
            return _videoRAM[(address - MemorySchema.VIDEO_RAM_START) + MemorySchema.MAX_VRAM_BANK_SIZE * bank];
        }
    }
}
