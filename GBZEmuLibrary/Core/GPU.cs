using System;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Security.Policy;

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
            Unused0,
            Unused1,
            Unused2,
            Unused3,
            PaletteNum,
            XFlip,
            YFlip,
            SpriteToBGPriority
        }

        public const int HORIZONTAL_RESOLUTION = 160;
        public const int VERTICAL_RESOLUTION   = 144;

        private const int SCALINE_DRAW_CLOCKS   = 456;
        private const int MAX_SCANLINES         = 153;
        private const int MAX_SCROLL_AMOUNT     = 256;

        private const int WINDOW_X_OFFSET = 7;

        private const int MAX_SPRITES = 40;

        private const int SEARCHING_SPRITES_ATTRIBUTES_BOUNDS    = SCALINE_DRAW_CLOCKS - 80;
        private const int TRANSFERRING_DATA_TO_LCD_DRIVER_BOUNDS = SEARCHING_SPRITES_ATTRIBUTES_BOUNDS - 172;

        private readonly byte[] _videoRAM = new byte[MemorySchema.VIDEO_RAM_END - MemorySchema.VIDEO_RAM_START];
        private readonly byte[] _gpuRegisters = new byte[MemorySchema.GPU_REGISTERS_END - MemorySchema.GPU_REGISTERS_START];
        private readonly byte[] _spriteAttributeTable = new byte[MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START];

        private int _scanlineCounter = SCALINE_DRAW_CLOCKS;

        // TODO make below configurable
        private Color[] _palette = new[]
        {
            new Color(224, 248, 208),
            new Color(136, 192, 112),
            new Color(52, 104, 86),
            new Color(8, 24, 32)
        };

        private Color[,] _screenData = new Color[HORIZONTAL_RESOLUTION, VERTICAL_RESOLUTION];

        public void Update(int cycles)
        {
            SetLCDStatus(cycles);

            if (!IsLCDEnabled())
            {
                return;
            }

            _scanlineCounter -= cycles;

            if (_scanlineCounter > 0)
            {
                return;
            }

            // Not 100% about this maybe just _scanlineCounter = SCALINE_DRAW_CLOCKS;
            _scanlineCounter = SCALINE_DRAW_CLOCKS + _scanlineCounter;

            var currentLine = _gpuRegisters[(int)Registers.Scanline];

            if (currentLine > MAX_SCANLINES)
            {
                _gpuRegisters[(int)Registers.Scanline] = 0;
                return;
            }

            if (currentLine == VERTICAL_RESOLUTION)
            {
                MessageBus.Instance.RequestInterrupt(Interrupts.VBlank);
            }

            if (currentLine < VERTICAL_RESOLUTION)
            {
                DrawScanLine();
            }

            _gpuRegisters[(int)Registers.Scanline]++;
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

            return _videoRAM[address - MemorySchema.VIDEO_RAM_START];
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
            else
            {
                _videoRAM[address - MemorySchema.VIDEO_RAM_START] = data;
            }
        }

        public Color[,] GetScreenData()
        {
            return _screenData;
        }

        private void SetLCDStatus(int cycles)
        {
            if (!IsLCDEnabled())
            {
                _scanlineCounter = SCALINE_DRAW_CLOCKS - cycles;
                _gpuRegisters[(int)Registers.Scanline] = 0;
                SetStatusRegister(LCDStatus.VBlank);
                return;
            }

            var currentLine = _gpuRegisters[(int)Registers.Scanline];
            var mode = LCDStatus.HBlank;
            var requestInterrupt = false;

            if (currentLine >= VERTICAL_RESOLUTION)
            {
                mode = LCDStatus.VBlank;
                SetStatusRegister(mode);
                requestInterrupt = Helpers.TestBit(_gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.VBlankInterruptEnabled);
            }
            else if (_scanlineCounter >= SEARCHING_SPRITES_ATTRIBUTES_BOUNDS)
            {
                mode = LCDStatus.SearchingSpritesAttributes;
                SetStatusRegister(mode);
                requestInterrupt = Helpers.TestBit(_gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.SearchingSpriteAttributesInterruptEnabled);
            }
            else if (_scanlineCounter >= TRANSFERRING_DATA_TO_LCD_DRIVER_BOUNDS)
            {
                mode = LCDStatus.TransferringDataToLCDDriver;
                SetStatusRegister(mode);
            }
            else
            {
                SetStatusRegister(mode);
                requestInterrupt = Helpers.TestBit(_gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.HBlankInterruptEnabled);
            }

            var coincidence = currentLine == _gpuRegisters[(int)Registers.LCDYCoord];
            Helpers.SetBit(ref _gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.Coincidence, coincidence);

            //Request interrupt if mode has changed or a coincidence has occurred
            if ((requestInterrupt && (int)mode != (_gpuRegisters[(int)Registers.LCDStatus] & 0x3)) || (coincidence && Helpers.TestBit(_gpuRegisters[(int)Registers.LCDStatus], (int)LCDStatusBits.CoincidenceInterruptEnabled)))
            {
                MessageBus.Instance.RequestInterrupt(Interrupts.LCD);
            }
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
                RenderTiles();
            }

            if (Helpers.TestBit(control, (int)LCDControlBits.SpriteDisplayEnabled))
            {
                RenderSprites();
            }
        }

        private void RenderTiles()
        {
            var control = _gpuRegisters[(int)Registers.LCDControl];
            var scanline = _gpuRegisters[(int)Registers.Scanline];

            var windowY = _gpuRegisters[(int)Registers.WindowY];
            var windowX = _gpuRegisters[(int)Registers.WindowX] - WINDOW_X_OFFSET;
            var scrollY = _gpuRegisters[(int)Registers.ScrollY];
            var scrollX = _gpuRegisters[(int)Registers.ScrollX];

            var usingWindow = Helpers.TestBit(control, (int)LCDControlBits.WindowDisplayEnabled) && windowY < scanline;

            var tileData = Helpers.TestBit(control, (int)LCDControlBits.BGWindowTileDataSelect) ? MemorySchema.TILE_DATA_UNSIGNED_START : MemorySchema.TILE_DATA_SIGNED_START;

            var backgroundMemory = Helpers.TestBit(control, usingWindow ? (int)LCDControlBits.WindowTileMapSelect : (int)LCDControlBits.BGTileMapSelect) ? MemorySchema.BACKGROUND_LAYOUT_1_START : MemorySchema.BACKGROUND_LAYOUT_0_START;

            var yPos = usingWindow ? (scanline - windowY) : (scrollY + scanline) % MAX_SCROLL_AMOUNT;

            //TODO get rid of below magic numbers
            var tileRow = ((byte)(yPos / 8)) * 32;

            for (var pixel = 0; pixel < HORIZONTAL_RESOLUTION; pixel++)
            {
                var xPos = usingWindow && pixel > windowX ? (pixel - windowX) : (pixel + scrollX) % MAX_SCROLL_AMOUNT;

                var tileCol = xPos / 8;

                var signed = tileData == MemorySchema.TILE_DATA_SIGNED_START;
                var data = ReadByte(backgroundMemory + tileRow + tileCol);
                var tileNum = signed ? (int)(sbyte)data : data;

                var tileLoc = tileData + (signed ? (tileNum + 128) * 16 : tileNum * 16);

                var line = (yPos % 8) * 2;
                var data1 = ReadByte(tileLoc + line);
                var data2 = ReadByte(tileLoc + line + 1);

                var colorBit = ((xPos % 8) - 7) * -1;

                var colorNum = (Helpers.GetBit(data2, colorBit) << 1) | Helpers.GetBit(data1, colorBit);

                var colorIndex = GetColorIndex((byte)colorNum, MemorySchema.GPU_REGISTERS_START | (int)Registers.BackgroundTilePalette);

                if (scanline >= VERTICAL_RESOLUTION || pixel < 0 || pixel >= HORIZONTAL_RESOLUTION)
                {
                    continue;
                }

                _screenData[pixel, scanline] = _palette[colorIndex];
            }
        }

        private void RenderSprites()
        {
            var control = _gpuRegisters[(int)Registers.LCDControl];
            var scanline = _gpuRegisters[(int)Registers.Scanline];

            if (scanline == 113)
            {
                Helpers.NoOp();
            }

            var use8x16 = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize);
            var ySize = use8x16 ? 16 : 8;

            for (var i = (HORIZONTAL_RESOLUTION - 4); i >= 0; i -= 4)
            {
                const int tableStart = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START;
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
                    var tileAddress = (MemorySchema.TILE_DATA_UNSIGNED_START + tileIndex * 16); //16 bits per tile

                    var data1 = ReadByte(tileAddress + tileLineOffset);
                    var data2 = ReadByte(tileAddress + tileLineOffset + 1);
                    var paletteAddress = (Helpers.TestBit(attributes, (int)SpriteAttributesBits.PaletteNum) ? (int)Registers.SpritePalette1 : (int)Registers.SpritePalette0);

                    for (var column = 0; column < 8; column++)
                    {
                        var spriteX = x + column;

                        if (spriteX >= 0 && spriteX < HORIZONTAL_RESOLUTION && scanline < VERTICAL_RESOLUTION)
                        {
                            var tilePixelColumn = column;

                            if (Helpers.TestBit(attributes, (int)SpriteAttributesBits.XFlip))
                            {
                                tilePixelColumn = Math.Abs(tilePixelColumn - 7);
                            }

                            byte colorValue = 0;
                            colorValue |= (byte)((data1 >> (7 - tilePixelColumn)) & 1);
                            colorValue |= (byte)(((data2 >> (7 - tilePixelColumn)) & 1) << 1);

                            var colorIndex = GetColorIndex(colorValue, MemorySchema.GPU_REGISTERS_START | paletteAddress);

                            if (colorIndex == 0)
                            {
                                continue;
                            }

                            _screenData[spriteX, scanline] = _palette[colorIndex];
                        }
                    }
                }
            }

            /*var control = _gpuRegisters[(int)Registers.LCDControl];
            var scanline = _gpuRegisters[(int)Registers.Scanline];

            var use8x16 = Helpers.TestBit(control, (int)LCDControlBits.SpriteSize);

            for (var sprite = 0; sprite < MAX_SPRITES; sprite++)
            {
                var index = sprite * 4;

                const int tableStart = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START;

                var yPos = ReadByte(tableStart | index) - 16;
                var xPos = ReadByte(tableStart | (index + 1)) - 8;

                var tileLoc = ReadByte(tableStart | (index + 2));
                var attributes = ReadByte(tableStart | (index + 3));

                var yFlip = Helpers.TestBit(attributes, (int)SpriteAttributesBits.YFlip);
                var xFlip = Helpers.TestBit(attributes, (int)SpriteAttributesBits.XFlip);

                var ySize = use8x16 ? 16 : 8;

                if (scanline >= yPos && scanline < (yPos + ySize))
                {
                    var line = scanline - yPos;

                    if (yFlip)
                    {
                        line = (line - ySize) * -1;
                    }

                    line *= 2;

                    var dataAddress = MemorySchema.TILE_DATA_UNSIGNED_START + (tileLoc * 16) + line;
                    var data1 = ReadByte(dataAddress);
                    var data2 = ReadByte(dataAddress + 1);

                    for (var tilePixel = 7; tilePixel >= 0; tilePixel--)
                    {
                        var colorBit = tilePixel;

                        if (xFlip)
                        {
                            colorBit = (colorBit - 7) * -1;
                        }

                        var colorNum = (Helpers.GetBit(data2, colorBit) << 1) | Helpers.GetBit(data1, colorBit);

                        var colorIndex = GetColorIndex((byte)colorNum, MemorySchema.GPU_REGISTERS_START | (Helpers.TestBit(attributes, (int)SpriteAttributesBits.PaletteNum) ? (int)Registers.SpritePalette1 : (int)Registers.SpritePalette0));

                        var pixel = xPos + (-tilePixel + 7);

                        if (colorIndex == 0 || scanline >= VERTICAL_RESOLUTION || pixel < 0 || pixel >= HORIZONTAL_RESOLUTION || Helpers.TestBit(attributes, (int)SpriteAttributesBits.SpriteToBGPriority))
                        {
                            continue;
                        }

                        _screenData[pixel, scanline] = _palette[colorIndex];
                    }
                }
            }*/
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
    }
}