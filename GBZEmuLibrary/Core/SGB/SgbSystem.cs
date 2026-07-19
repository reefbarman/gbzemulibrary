using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// High-level emulation of the Super Game Boy ICD2 command channel, colorization state,
    /// VRAM transfers, multiplayer IDs, and SNES-side 256x224 composite output.
    /// </summary>
    internal sealed class SgbSystem
    {
        private enum Command
        {
            Pal01 = 0x00,
            Pal23 = 0x01,
            Pal03 = 0x02,
            Pal12 = 0x03,
            AttrBlock = 0x04,
            AttrLine = 0x05,
            AttrDivide = 0x06,
            AttrCharacters = 0x07,
            PalSet = 0x0A,
            PalTransfer = 0x0B,
            DataSound = 0x0F,
            MultiplayerRequest = 0x11,
            CharacterTransfer = 0x13,
            PictureTransfer = 0x14,
            AttributeTransfer = 0x15,
            AttributeSet = 0x16,
            Mask = 0x17
        }

        private enum MaskMode
        {
            Disabled,
            Freeze,
            Black,
            ColorZero
        }

        private enum TransferDestination
        {
            LowTiles,
            HighTiles,
            BorderData,
            Palettes,
            Attributes
        }

        private sealed class BorderState
        {
            public readonly byte[] Tiles = new byte[0x2000];
            public readonly ushort[] Map = new ushort[32 * 32];
            public readonly ushort[] Palettes = new ushort[16 * 4];

            public void Clear()
            {
                Array.Clear(Tiles, 0, Tiles.Length);
                Array.Clear(Map, 0, Map.Length);
                Array.Clear(Palettes, 0, Palettes.Length);
            }

            public void CopyFrom(BorderState source)
            {
                Array.Copy(source.Tiles, Tiles, Tiles.Length);
                Array.Copy(source.Map, Map, Map.Length);
                Array.Copy(source.Palettes, Palettes, Palettes.Length);
            }
        }

        private const int PacketSize = 16;
        private const int MaximumPackets = 7;

        private static readonly byte[] GlyphA = { 14, 17, 17, 31, 17, 17, 17 };
        private static readonly byte[] GlyphB = { 30, 17, 17, 30, 17, 17, 30 };
        private static readonly byte[] GlyphE = { 31, 16, 16, 30, 16, 16, 31 };
        private static readonly byte[] GlyphG = { 14, 17, 16, 23, 17, 17, 15 };
        private static readonly byte[] GlyphM = { 17, 27, 21, 21, 17, 17, 17 };
        private static readonly byte[] GlyphO = { 14, 17, 17, 17, 17, 17, 14 };
        private static readonly byte[] GlyphP = { 30, 17, 17, 30, 16, 16, 16 };
        private static readonly byte[] GlyphR = { 30, 17, 17, 30, 20, 18, 17 };
        private static readonly byte[] GlyphS = { 15, 16, 16, 14, 1, 1, 30 };
        private static readonly byte[] GlyphU = { 17, 17, 17, 17, 17, 17, 14 };
        private static readonly byte[] GlyphY = { 17, 17, 10, 4, 4, 4, 4 };
        private static readonly byte[] GlyphZ = { 31, 1, 2, 4, 8, 16, 31 };
        private static readonly byte[] Glyph2 = { 14, 17, 1, 2, 4, 8, 31 };

        // SameBoy-compatible automatic palettes for common non-SGB Nintendo titles.
        // RGB555 values and title identifiers are hardware-observed compatibility data.
        private static readonly ushort[] BuiltInPalettes =
        {
            0x67BF, 0x265B, 0x10B5, 0x2866, 0x637B, 0x3AD9, 0x0956, 0x0000,
            0x7F1F, 0x2A7D, 0x30F3, 0x4CE7, 0x57FF, 0x2618, 0x001F, 0x006A,
            0x5B7F, 0x3F0F, 0x222D, 0x10EB, 0x7FBB, 0x2A3C, 0x0015, 0x0900,
            0x2800, 0x7680, 0x01EF, 0x2FFF, 0x73BF, 0x46FF, 0x0110, 0x0066,
            0x533E, 0x2638, 0x01E5, 0x0000, 0x7FFF, 0x2BBF, 0x00DF, 0x2C0A,
            0x7F1F, 0x463D, 0x74CF, 0x4CA5, 0x53FF, 0x03E0, 0x00DF, 0x2800,
            0x433F, 0x72D2, 0x3045, 0x0822, 0x7FFA, 0x2A5F, 0x0014, 0x0003,
            0x1EED, 0x215C, 0x42FC, 0x0060, 0x7FFF, 0x5EF7, 0x39CE, 0x0000,
            0x4F5F, 0x630E, 0x159F, 0x3126, 0x637B, 0x121C, 0x0140, 0x0840,
            0x66BC, 0x3FFF, 0x7EE0, 0x2C84, 0x5FFE, 0x3EBC, 0x0321, 0x0000,
            0x63FF, 0x36DC, 0x11F6, 0x392A, 0x65EF, 0x7DBF, 0x035F, 0x2108,
            0x2B6C, 0x7FFF, 0x1CD9, 0x0007, 0x53FC, 0x1F2F, 0x0E29, 0x0061,
            0x36BE, 0x7EAF, 0x681A, 0x3C00, 0x7BBE, 0x329D, 0x1DE8, 0x0423,
            0x739F, 0x6A9B, 0x7293, 0x0001, 0x5FFF, 0x6732, 0x3DA9, 0x2481,
            0x577F, 0x3EBC, 0x456F, 0x1880, 0x6B57, 0x6E1B, 0x5010, 0x0007,
            0x0F96, 0x2C97, 0x0045, 0x3200, 0x67FF, 0x2F17, 0x2230, 0x1548
        };

        private static readonly string[] BuiltInPaletteTitles =
        {
            "ZELDA", "SUPER MARIOLAND", "MARIOLAND2", "SUPERMARIOLAND3",
            "KIRBY DREAM LAND", "HOSHINOKA-BI", "KIRBY'S PINBALL", "YOSSY NO TAMAGO",
            "MARIO & YOSHI", "YOSSY NO COOKIE", "YOSHI'S COOKIE", "DR.MARIO",
            "TETRIS", "YAKUMAN", "METROID2", "KAERUNOTAMENI", "GOLF", "ALLEY WAY",
            "BASEBALL", "TENNIS", "F1RACE", "KID ICARUS", "QIX", "SOLARSTRIKER",
            "X", "GBWARS"
        };

        private static readonly byte[] BuiltInPaletteIds =
        {
            5, 6, 0x14, 2, 0x0B, 0x0B, 3, 0x0C, 0x0C, 4, 4, 0x12, 0x11,
            0x13, 0x1F, 9, 0x18, 0x16, 0x0F, 0x17, 0x1E, 0x0E, 0x19, 7, 0x1C, 0x15
        };

        private readonly GPU _gpu;
        private readonly byte[] _command = new byte[PacketSize * MaximumPackets];
        private readonly byte[] _effectiveScreen = new byte[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
        private readonly ushort[] _effectivePalettes = new ushort[4 * 4];
        private readonly ushort[] _ramPalettes = new ushort[4 * 512];
        private readonly byte[] _attributeMap = new byte[20 * 18];
        private readonly byte[] _attributeFiles = new byte[0xFE0];
        private readonly byte[] _receivedHeader = new byte[0x54];
        private readonly BorderState _border = new BorderState();
        private readonly BorderState _pendingBorder = new BorderState();
        private readonly Color[,] _screenData = new Color[
            SuperGameBoyDisplay.HORIZONTAL_RESOLUTION,
            SuperGameBoyDisplay.VERTICAL_RESOLUTION];

        private SgbModel _model;
        private MaskMode _maskMode;
        private TransferDestination _transferDestination;
        private int _commandWriteIndex;
        private int _vramTransferCountdown;
        private int _playerCount;
        private int _currentPlayer;
        private bool _readyForPulse;
        private bool _readyForWrite;
        private bool _readyForStop;
        private bool _disableCommands;
        private bool _hasGameBorder;

        public bool Enabled => _model != SgbModel.None;
        public int PlayerCount => _playerCount;
        public int CurrentPlayer => _currentPlayer;

        /// <summary>
        /// Creates an SGB bridge attached to the completed DMG pixel output of one PPU.
        /// </summary>
        public SgbSystem(GPU gpu)
        {
            _gpu = gpu;
        }

        /// <summary>
        /// Resets all SGB-side state and validates the cartridge immediately when firmware is skipped.
        /// </summary>
        public void Reset(SgbModel model, byte[] rom, bool usingBootROM)
        {
            _model = model;
            _maskMode = MaskMode.Disabled;
            _transferDestination = TransferDestination.LowTiles;
            _commandWriteIndex = 0;
            _vramTransferCountdown = 0;
            _playerCount = 1;
            _currentPlayer = 0;
            _readyForPulse = false;
            _readyForWrite = false;
            _readyForStop = false;
            _disableCommands = false;
            _hasGameBorder = false;

            Array.Clear(_command, 0, _command.Length);
            Array.Clear(_effectiveScreen, 0, _effectiveScreen.Length);
            Array.Clear(_effectivePalettes, 0, _effectivePalettes.Length);
            Array.Clear(_ramPalettes, 0, _ramPalettes.Length);
            Array.Clear(_attributeMap, 0, _attributeMap.Length);
            Array.Clear(_attributeFiles, 0, _attributeFiles.Length);
            Array.Clear(_receivedHeader, 0, _receivedHeader.Length);
            _border.Clear();
            _pendingBorder.Clear();

            if (!Enabled)
            {
                return;
            }

            // Original GBZEmu replacement palette, used until a game supplies its own SGB palettes.
            SetPalette(0, 0x7FFF, 0x42B5, 0x214A, 0x0000);
            for (var palette = 1; palette < 4; palette++)
            {
                Array.Copy(_effectivePalettes, 0, _effectivePalettes, palette * 4, 4);
            }

            if (!usingBootROM)
            {
                _disableCommands = !HasValidSgbHeader(rom);
                if (_disableCommands)
                {
                    ApplyBuiltInTitlePalette(rom, 0x134);
                }
            }

            ComposeFrame();
        }

        /// <summary>
        /// Receives writes to the JOYP selection lines and reconstructs SGB packets LSB-first.
        /// </summary>
        public void WriteJoypad(byte value, byte previousValue)
        {
            if (!Enabled || _disableCommands)
            {
                return;
            }

            if ((value & 0x20) != 0 && (previousValue & 0x20) == 0 && (_playerCount & 1) == 0)
            {
                _currentPlayer = (_currentPlayer + 1) & (_playerCount - 1);
            }

            var expectedBits = Math.Max(1, _command[0] & 7) * PacketSize * 8;
            if ((_command[0] & 0xF1) == 0xF1)
            {
                expectedBits = PacketSize * 8;
            }

            switch ((value >> 4) & 3)
            {
                case 3:
                    _readyForPulse = true;
                    return;
                case 2:
                    ReceiveBit(false, expectedBits);
                    return;
                case 1:
                    ReceiveBit(true, expectedBits);
                    return;
                case 0:
                    // Both select lines low are the ICD2 reset/start pulse. Treat it as an
                    // unconditional receiver reset so the first packet after power-on does not
                    // depend on a synthetic earlier high pulse from the host.
                    ResetPacketReceiver();
                    _readyForWrite = true;
                    return;
            }
        }

        /// <summary>
        /// Returns the active-low multiplayer controller ID while both input groups are deselected.
        /// </summary>
        public byte GetPlayerId()
        {
            return (byte)(_playerCount > 1 ? 0x0F - _currentPlayer : 0x0F);
        }

        /// <summary>
        /// Processes delayed SGB VRAM transfers and publishes the current 256x224 composite frame.
        /// </summary>
        public void FrameCompleted()
        {
            if (!Enabled)
            {
                return;
            }

            if (_maskMode != MaskMode.Freeze)
            {
                var source = _gpu.GetScreenData();
                var destination = 0;
                for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
                {
                    for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
                    {
                        _effectiveScreen[destination++] = source[x, y].SgbIndex;
                    }
                }
            }

            if (_vramTransferCountdown > 0 && --_vramTransferCountdown == 0)
            {
                CompleteVramTransfer();
            }

            ComposeFrame();
        }

        /// <summary>
        /// Returns the reusable SGB composite framebuffer. The caller must not mutate it.
        /// </summary>
        public Color[,] GetScreenData()
        {
            return _screenData;
        }

        private void ReceiveBit(bool one, int expectedBits)
        {
            if (!_readyForPulse || !_readyForWrite)
            {
                return;
            }

            if (_readyForStop)
            {
                if (one)
                {
                    ResetPacketReceiver();
                    return;
                }

                if (_commandWriteIndex == expectedBits)
                {
                    CommandReady();
                    _commandWriteIndex = 0;
                    Array.Clear(_command, 0, _command.Length);
                }

                _readyForPulse = false;
                _readyForWrite = false;
                _readyForStop = false;
                return;
            }

            if (_commandWriteIndex < _command.Length * 8)
            {
                if (one)
                {
                    _command[_commandWriteIndex / 8] |= (byte)(1 << (_commandWriteIndex & 7));
                }

                _commandWriteIndex++;
                _readyForPulse = false;
                if ((_commandWriteIndex & (PacketSize * 8 - 1)) == 0)
                {
                    _readyForStop = true;
                }
            }
        }

        private void ResetPacketReceiver()
        {
            _commandWriteIndex = 0;
            _readyForPulse = false;
            _readyForWrite = false;
            _readyForStop = false;
            Array.Clear(_command, 0, _command.Length);
        }

        private void CommandReady()
        {
            if ((_command[0] & 0xF1) == 0xF1)
            {
                ReceiveBootHeaderPacket();
                return;
            }

            if ((_command[0] & 7) == 0)
            {
                return;
            }

            switch ((Command)(_command[0] >> 3))
            {
                case Command.Pal01:
                    ApplyDirectPalette(0, 1);
                    break;
                case Command.Pal23:
                    ApplyDirectPalette(2, 3);
                    break;
                case Command.Pal03:
                    ApplyDirectPalette(0, 3);
                    break;
                case Command.Pal12:
                    ApplyDirectPalette(1, 2);
                    break;
                case Command.AttrBlock:
                    ApplyAttributeBlocks();
                    break;
                case Command.AttrLine:
                    ApplyAttributeLines();
                    break;
                case Command.AttrDivide:
                    ApplyAttributeDivide();
                    break;
                case Command.AttrCharacters:
                    ApplyAttributeCharacters();
                    break;
                case Command.PalSet:
                    ApplyPaletteSet();
                    break;
                case Command.PalTransfer:
                    StartTransfer(TransferDestination.Palettes);
                    break;
                case Command.DataSound:
                    break;
                case Command.MultiplayerRequest:
                    var multiplayerMode = _command[1] & 3;
                    _playerCount = multiplayerMode + 1;
                    if (multiplayerMode == 2)
                    {
                        // The unsupported mode has a hardware-observed off-by-one mask that
                        // exposes only controller IDs one and three. SameSuite relies on this
                        // quirk even though ordinary software requests one, two, or four pads.
                        _currentPlayer = (_currentPlayer + 1) & 2;
                    }
                    else
                    {
                        _currentPlayer &= _playerCount - 1;
                    }
                    break;
                case Command.CharacterTransfer:
                    StartTransfer((_command[1] & 1) == 0
                        ? TransferDestination.LowTiles
                        : TransferDestination.HighTiles);
                    break;
                case Command.PictureTransfer:
                    StartTransfer(TransferDestination.BorderData);
                    break;
                case Command.AttributeTransfer:
                    StartTransfer(TransferDestination.Attributes);
                    break;
                case Command.AttributeSet:
                    LoadAttributeFile(_command[1] & 0x3F);
                    if ((_command[1] & 0x40) != 0)
                    {
                        _maskMode = MaskMode.Disabled;
                    }
                    break;
                case Command.Mask:
                    _maskMode = (MaskMode)(_command[1] & 3);
                    break;
            }
        }

        private void ReceiveBootHeaderPacket()
        {
            byte checksum = 0;
            for (var index = 2; index < PacketSize; index++)
            {
                checksum += _command[index];
            }

            if (checksum != _command[1])
            {
                _disableCommands = true;
                return;
            }

            var packet = (_command[0] >> 1) & 7;
            if (packet > 5)
            {
                return;
            }

            Array.Copy(_command, 2, _receivedHeader, packet * 14, 14);
            if (_command[0] == 0xFB)
            {
                _disableCommands = _receivedHeader[0x42] != 3 || _receivedHeader[0x47] != 0x33;
                if (_disableCommands)
                {
                    ApplyBuiltInTitlePalette(_receivedHeader, 0x30);
                }
            }
        }

        private void ApplyBuiltInTitlePalette(byte[] bytes, int titleOffset)
        {
            if (bytes == null || titleOffset + 16 > bytes.Length)
            {
                return;
            }

            for (var titleIndex = 0; titleIndex < BuiltInPaletteTitles.Length; titleIndex++)
            {
                var title = BuiltInPaletteTitles[titleIndex];
                var matches = true;
                for (var character = 0; character < 16; character++)
                {
                    var expected = character < title.Length ? (byte)title[character] : (byte)0;
                    if (bytes[titleOffset + character] != expected)
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                var source = (BuiltInPaletteIds[titleIndex] - 1) * 4;
                Array.Copy(BuiltInPalettes, source, _effectivePalettes, 0, 4);
                for (var palette = 1; palette < 4; palette++)
                {
                    Array.Copy(_effectivePalettes, 0, _effectivePalettes, palette * 4, 4);
                }
                return;
            }
        }

        private void ApplyDirectPalette(int first, int second)
        {
            var colorZero = ReadUInt16(_command, 1);
            for (var palette = 0; palette < 4; palette++)
            {
                _effectivePalettes[palette * 4] = colorZero;
            }

            for (var color = 1; color < 4; color++)
            {
                _effectivePalettes[first * 4 + color] = ReadUInt16(_command, 1 + color * 2);
                _effectivePalettes[second * 4 + color] = ReadUInt16(_command, 7 + color * 2);
            }
        }

        private void ApplyAttributeBlocks()
        {
            var count = Math.Min(_command[1], (byte)0x12);
            for (var block = 0; block < count; block++)
            {
                var offset = 2 + block * 6;
                if (offset + 5 >= _command.Length)
                {
                    return;
                }

                var control = _command[offset];
                var palettes = _command[offset + 1];
                var left = _command[offset + 2] & 0x1F;
                var top = _command[offset + 3] & 0x1F;
                var right = _command[offset + 4] & 0x1F;
                var bottom = _command[offset + 5] & 0x1F;
                var inside = (control & 1) != 0;
                var boundary = (control & 2) != 0;
                var outside = (control & 4) != 0;
                var insidePalette = palettes & 3;
                var boundaryPalette = (palettes >> 2) & 3;
                var outsidePalette = (palettes >> 4) & 3;

                if (inside && !boundary && !outside)
                {
                    boundary = true;
                    boundaryPalette = insidePalette;
                }
                else if (outside && !boundary && !inside)
                {
                    boundary = true;
                    boundaryPalette = outsidePalette;
                }

                for (var y = 0; y < 18; y++)
                {
                    for (var x = 0; x < 20; x++)
                    {
                        var outsideRectangle = x < left || x > right || y < top || y > bottom;
                        var insideRectangle = x > left && x < right && y > top && y < bottom;
                        if (outsideRectangle && outside)
                        {
                            _attributeMap[x + y * 20] = (byte)outsidePalette;
                        }
                        else if (insideRectangle && inside)
                        {
                            _attributeMap[x + y * 20] = (byte)insidePalette;
                        }
                        else if (!outsideRectangle && !insideRectangle && boundary)
                        {
                            _attributeMap[x + y * 20] = (byte)boundaryPalette;
                        }
                    }
                }
            }
        }

        private void ApplyAttributeLines()
        {
            var count = Math.Min(_command[1], (byte)(_command.Length - 2));
            for (var index = 0; index < count; index++)
            {
                var data = _command[index + 2];
                var horizontal = (data & 0x80) != 0;
                var palette = (byte)((data >> 5) & 3);
                var line = data & 0x1F;
                if (horizontal && line < 18)
                {
                    for (var x = 0; x < 20; x++)
                    {
                        _attributeMap[x + line * 20] = palette;
                    }
                }
                else if (!horizontal && line < 20)
                {
                    for (var y = 0; y < 18; y++)
                    {
                        _attributeMap[line + y * 20] = palette;
                    }
                }
            }
        }

        private void ApplyAttributeDivide()
        {
            var rightBottomPalette = _command[1] & 3;
            var leftTopPalette = (_command[1] >> 2) & 3;
            var linePalette = (_command[1] >> 4) & 3;
            var horizontal = (_command[1] & 0x40) != 0;
            var line = _command[2] & 0x1F;

            for (var y = 0; y < 18; y++)
            {
                for (var x = 0; x < 20; x++)
                {
                    var coordinate = horizontal ? y : x;
                    _attributeMap[x + y * 20] = (byte)(coordinate < line
                        ? leftTopPalette
                        : coordinate == line ? linePalette : rightBottomPalette);
                }
            }
        }

        private void ApplyAttributeCharacters()
        {
            var x = _command[1];
            var y = _command[2];
            var count = ReadUInt16(_command, 3);
            var vertical = _command[5] != 0;
            if (x >= 20 || y >= 18)
            {
                return;
            }

            for (var index = 0; index < count && 6 + index / 4 < _command.Length; index++)
            {
                var palette = (_command[6 + index / 4] >> ((3 - (index & 3)) * 2)) & 3;
                _attributeMap[x + y * 20] = (byte)palette;
                if (vertical)
                {
                    if (++y == 18)
                    {
                        y = 0;
                        if (++x == 20)
                        {
                            return;
                        }
                    }
                }
                else if (++x == 20)
                {
                    x = 0;
                    if (++y == 18)
                    {
                        return;
                    }
                }
            }
        }

        private void ApplyPaletteSet()
        {
            for (var palette = 0; palette < 4; palette++)
            {
                var idOffset = 1 + palette * 2;
                var paletteId = _command[idOffset] | ((_command[idOffset + 1] & 1) << 8);
                Array.Copy(_ramPalettes, paletteId * 4, _effectivePalettes, palette * 4, 4);
            }

            _effectivePalettes[4] = _effectivePalettes[0];
            _effectivePalettes[8] = _effectivePalettes[0];
            _effectivePalettes[12] = _effectivePalettes[0];
            if ((_command[9] & 0x80) != 0)
            {
                LoadAttributeFile(_command[9] & 0x3F);
            }
            if ((_command[9] & 0x40) != 0)
            {
                _maskMode = MaskMode.Disabled;
            }
        }

        private void LoadAttributeFile(int index)
        {
            if (index > 0x2C)
            {
                return;
            }

            var output = 0;
            for (var byteIndex = 0; byteIndex < 90; byteIndex++)
            {
                var data = _attributeFiles[index * 90 + byteIndex];
                for (var pair = 0; pair < 4; pair++)
                {
                    _attributeMap[output++] = (byte)(data >> 6);
                    data <<= 2;
                }
            }
        }

        private void StartTransfer(TransferDestination destination)
        {
            _vramTransferCountdown = 3;
            _transferDestination = destination;
        }

        private void CompleteVramTransfer()
        {
            var tileCount = _transferDestination == TransferDestination.BorderData ? 0x88
                : _transferDestination == TransferDestination.Attributes ? 0xFE
                : 0x100;
            var outputWord = 0;

            for (var tile = 0; tile < tileCount; tile++)
            {
                var tileX = (tile % 20) * 8;
                var tileY = (tile / 20) * 8;
                for (var y = 0; y < 8; y++)
                {
                    ushort word = 0;
                    for (var x = 0; x < 8; x++)
                    {
                        var pixel = _effectiveScreen[tileX + x + (tileY + y) * Display.HORIZONTAL_RESOLUTION] & 3;
                        if ((pixel & 1) != 0)
                        {
                            word |= (ushort)(0x0080 >> x);
                        }
                        if ((pixel & 2) != 0)
                        {
                            word |= (ushort)(0x8000 >> x);
                        }
                    }

                    StoreTransferWord(outputWord++, word);
                }
            }

            if (_transferDestination == TransferDestination.BorderData)
            {
                _border.CopyFrom(_pendingBorder);
                _hasGameBorder = true;
            }
        }

        private void StoreTransferWord(int index, ushort value)
        {
            switch (_transferDestination)
            {
                case TransferDestination.LowTiles:
                case TransferDestination.HighTiles:
                    var byteOffset = (_transferDestination == TransferDestination.HighTiles ? 0x1000 : 0) + index * 2;
                    _pendingBorder.Tiles[byteOffset] = (byte)value;
                    _pendingBorder.Tiles[byteOffset + 1] = (byte)(value >> 8);
                    break;
                case TransferDestination.BorderData:
                    if (index < _pendingBorder.Map.Length)
                    {
                        _pendingBorder.Map[index] = value;
                    }
                    else if (index - _pendingBorder.Map.Length < _pendingBorder.Palettes.Length)
                    {
                        _pendingBorder.Palettes[index - _pendingBorder.Map.Length] = value;
                    }
                    break;
                case TransferDestination.Palettes:
                    if (index < _ramPalettes.Length)
                    {
                        _ramPalettes[index] = value;
                    }
                    break;
                case TransferDestination.Attributes:
                    var offset = index * 2;
                    if (offset < _attributeFiles.Length)
                    {
                        _attributeFiles[offset] = (byte)value;
                    }
                    if (offset + 1 < _attributeFiles.Length)
                    {
                        _attributeFiles[offset + 1] = (byte)(value >> 8);
                    }
                    break;
            }
        }

        private void ComposeFrame()
        {
            if (!Enabled)
            {
                return;
            }

            var backdrop = ConvertRgb555(_effectivePalettes[0]);
            Fill(backdrop);
            RenderGameBoyScreen();
            if (_hasGameBorder)
            {
                RenderGameBorder(backdrop);
            }
            else
            {
                RenderDefaultBorder();
            }
        }

        private void RenderGameBoyScreen()
        {
            var black = ConvertRgb555(0);
            var colorZero = ConvertRgb555(_effectivePalettes[0]);
            var source = 0;
            for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
            {
                for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
                {
                    Color color;
                    if (_maskMode == MaskMode.Black)
                    {
                        color = black;
                    }
                    else if (_maskMode == MaskMode.ColorZero)
                    {
                        color = colorZero;
                    }
                    else
                    {
                        var palette = _attributeMap[x / 8 + (y / 8) * 20] & 3;
                        var shade = _effectiveScreen[source] & 3;
                        color = ConvertRgb555(_effectivePalettes[palette * 4 + shade]);
                    }

                    _screenData[x + SuperGameBoyDisplay.GAME_BOY_X, y + SuperGameBoyDisplay.GAME_BOY_Y] = color;
                    source++;
                }
            }
        }

        private void RenderGameBorder(Color backdrop)
        {
            for (var tileY = 0; tileY < 28; tileY++)
            {
                for (var tileX = 0; tileX < 32; tileX++)
                {
                    var gameArea = tileX >= 6 && tileX < 26 && tileY >= 5 && tileY < 23;
                    var entry = _border.Map[tileX + tileY * 32];
                    if ((entry & 0x0300) != 0)
                    {
                        continue;
                    }

                    var tile = entry & 0xFF;
                    var palette = (entry >> 10) & 3;
                    var flipX = (entry & 0x4000) != 0;
                    var flipY = (entry & 0x8000) != 0;
                    for (var y = 0; y < 8; y++)
                    {
                        var sourceY = flipY ? 7 - y : y;
                        var baseOffset = tile * 32 + sourceY * 2;
                        for (var x = 0; x < 8; x++)
                        {
                            var sourceX = flipX ? x : 7 - x;
                            var bit = 1 << sourceX;
                            var colorIndex = ((_border.Tiles[baseOffset] & bit) != 0 ? 1 : 0) |
                                             ((_border.Tiles[baseOffset + 1] & bit) != 0 ? 2 : 0) |
                                             ((_border.Tiles[baseOffset + 16] & bit) != 0 ? 4 : 0) |
                                             ((_border.Tiles[baseOffset + 17] & bit) != 0 ? 8 : 0);
                            var outputX = tileX * 8 + x;
                            var outputY = tileY * 8 + y;
                            if (colorIndex == 0)
                            {
                                if (!gameArea)
                                {
                                    _screenData[outputX, outputY] = backdrop;
                                }
                            }
                            else
                            {
                                _screenData[outputX, outputY] = ConvertRgb555(_border.Palettes[palette * 16 + colorIndex]);
                            }
                        }
                    }
                }
            }
        }

        private void RenderDefaultBorder()
        {
            var outer = new Color(9, 12, 28);
            var inner = new Color(24, 31, 58);
            var bevel = new Color(63, 76, 112);
            var highlight = new Color(78, 225, 205);
            var accent = new Color(244, 184, 69);

            for (var y = 0; y < SuperGameBoyDisplay.VERTICAL_RESOLUTION; y++)
            {
                for (var x = 0; x < SuperGameBoyDisplay.HORIZONTAL_RESOLUTION; x++)
                {
                    var insideGame = x >= SuperGameBoyDisplay.GAME_BOY_X &&
                                     x < SuperGameBoyDisplay.GAME_BOY_X + Display.HORIZONTAL_RESOLUTION &&
                                     y >= SuperGameBoyDisplay.GAME_BOY_Y &&
                                     y < SuperGameBoyDisplay.GAME_BOY_Y + Display.VERTICAL_RESOLUTION;
                    if (insideGame)
                    {
                        continue;
                    }

                    var edge = Math.Min(Math.Min(x, 255 - x), Math.Min(y, 223 - y));
                    _screenData[x, y] = edge < 5 ? outer : edge < 10 ? bevel : inner;
                }
            }

            DrawRectangle(44, 36, 168, 4, highlight);
            DrawRectangle(44, 184, 168, 4, accent);
            DrawRectangle(44, 40, 4, 144, highlight);
            DrawRectangle(208, 40, 4, 144, accent);
            DrawText("GBZEMU", 101, 14, 1, highlight);
            DrawText("SUPER GAME BOY", 86, 199, 1, accent);

            // SGB2 keeps the same custom system border but advertises its model discreetly.
            if (_model == SgbModel.Sgb2)
            {
                DrawText("2", 169, 199, 1, highlight);
            }
        }

        private void Fill(Color color)
        {
            for (var y = 0; y < SuperGameBoyDisplay.VERTICAL_RESOLUTION; y++)
            {
                for (var x = 0; x < SuperGameBoyDisplay.HORIZONTAL_RESOLUTION; x++)
                {
                    _screenData[x, y] = color;
                }
            }
        }

        private void DrawRectangle(int x, int y, int width, int height, Color color)
        {
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    _screenData[x + column, y + row] = color;
                }
            }
        }

        private void DrawText(string text, int x, int y, int scale, Color color)
        {
            for (var character = 0; character < text.Length; character++)
            {
                for (var row = 0; row < 7; row++)
                {
                    var bits = GetGlyphRow(text[character], row);
                    for (var column = 0; column < 5; column++)
                    {
                        if ((bits & (1 << (4 - column))) == 0)
                        {
                            continue;
                        }

                        DrawRectangle(x + character * 6 * scale + column * scale, y + row * scale, scale, scale, color);
                    }
                }
            }
        }

        private static byte GetGlyphRow(char character, int row)
        {
            switch (character)
            {
                case 'A': return GlyphA[row];
                case 'B': return GlyphB[row];
                case 'E': return GlyphE[row];
                case 'G': return GlyphG[row];
                case 'M': return GlyphM[row];
                case 'O': return GlyphO[row];
                case 'P': return GlyphP[row];
                case 'R': return GlyphR[row];
                case 'S': return GlyphS[row];
                case 'U': return GlyphU[row];
                case 'Y': return GlyphY[row];
                case 'Z': return GlyphZ[row];
                case '2': return Glyph2[row];
                default: return 0;
            }
        }

        private void SetPalette(int palette, ushort color0, ushort color1, ushort color2, ushort color3)
        {
            var offset = palette * 4;
            _effectivePalettes[offset] = color0;
            _effectivePalettes[offset + 1] = color1;
            _effectivePalettes[offset + 2] = color2;
            _effectivePalettes[offset + 3] = color3;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | bytes[offset + 1] << 8);
        }

        private static Color ConvertRgb555(ushort value)
        {
            return new Color(
                ExpandFiveBit(value & 0x1F),
                ExpandFiveBit((value >> 5) & 0x1F),
                ExpandFiveBit((value >> 10) & 0x1F));
        }

        private static byte ExpandFiveBit(int value)
        {
            return (byte)((value << 3) | (value >> 2));
        }

        private static bool HasValidSgbHeader(byte[] rom)
        {
            return rom != null && rom.Length > 0x14B && rom[0x146] == 3 && rom[0x14B] == 0x33;
        }
    }
}
