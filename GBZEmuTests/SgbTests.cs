using GBZEmuLibrary;
using GBZEmuFrontend;

namespace GBZEmuTests;

/// <summary>
/// Covers the public SGB hardware profile and the HLE JOYP command bridge used by compatible games.
/// </summary>
public sealed class SgbTests
{
    [Theory]
    [InlineData(BootMode.SGB, 0x01)]
    [InlineData(BootMode.SGB2, 0xFF)]
    public void BuiltInSgbBootRomHandsOffWithModelRegisters(BootMode mode, int expectedAccumulator)
    {
        using var rom = CreateSgbRom();
        var emulator = Start(rom, mode);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x100, 100));
        var cpu = emulator.Debug.GetCpuState();
        Assert.Equal((ushort)(expectedAccumulator << 8), cpu.AF);
        Assert.Equal(0x0014, cpu.BC);
        Assert.Equal(0x0000, cpu.DE);
        Assert.Equal(0xC060, cpu.HL);
        Assert.Equal(0xFFFE, cpu.SP);
        Assert.True(emulator.IsSuperGameBoy);
        emulator.Terminate();
    }

    [Fact]
    public void SgbAddsCompositeFrameWithoutChangingRawFramebufferContract()
    {
        using var rom = CreateSgbRom();
        var emulator = Start(rom, BootMode.SGB | BootMode.Skip);

        Assert.Equal(Display.HORIZONTAL_RESOLUTION, emulator.GetScreenData().GetLength(0));
        Assert.Equal(Display.VERTICAL_RESOLUTION, emulator.GetScreenData().GetLength(1));
        Assert.Equal(SuperGameBoyDisplay.HORIZONTAL_RESOLUTION, emulator.GetSuperGameBoyScreenData().GetLength(0));
        Assert.Equal(SuperGameBoyDisplay.VERTICAL_RESOLUTION, emulator.GetSuperGameBoyScreenData().GetLength(1));
        emulator.Terminate();
    }

    [Fact]
    public void JoypadPacketChangesSharedSgbColorZero()
    {
        var bus = new MessageBus();
        var gpu = new GPU(bus);
        var sgb = new SgbSystem(gpu);
        var joypad = new Joypad(bus, sgb);
        sgb.Reset(SgbModel.Sgb, CreateValidHeader(), usingBootROM: false);

        var packet = new byte[16];
        packet[0] = 0x01; // PAL01, one packet
        packet[1] = 0x1F; // RGB555 red, little endian
        SendPacket(joypad, packet);
        sgb.FrameCompleted();

        var pixel = sgb.GetScreenData()[SuperGameBoyDisplay.GAME_BOY_X, SuperGameBoyDisplay.GAME_BOY_Y];
        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    [Fact]
    public void MultiplayerRequestCyclesActiveControllerId()
    {
        var bus = new MessageBus();
        var gpu = new GPU(bus);
        var sgb = new SgbSystem(gpu);
        var joypad = new Joypad(bus, sgb);
        sgb.Reset(SgbModel.Sgb, CreateValidHeader(), usingBootROM: false);

        var packet = new byte[16];
        packet[0] = 0x89; // MLT_REQ, one packet
        packet[1] = 0x01; // two controllers
        SendPacket(joypad, packet);

        joypad.WriteByte(0x30, MemorySchema.JOYPAD_REGISTER);
        Assert.Equal(0x0F, joypad.ReadByte(MemorySchema.JOYPAD_REGISTER) & 0x0F);
        joypad.WriteByte(0x10, MemorySchema.JOYPAD_REGISTER);
        joypad.WriteByte(0x30, MemorySchema.JOYPAD_REGISTER);
        Assert.Equal(0x0E, joypad.ReadByte(MemorySchema.JOYPAD_REGISTER) & 0x0F);
    }

    [Fact]
    public void CharacterAndPictureTransfersReplaceTheFallbackBorder()
    {
        var bus = new MessageBus();
        var gpu = new GPU(bus);
        var sgb = new SgbSystem(gpu);
        var joypad = new Joypad(bus, sgb);
        sgb.Reset(SgbModel.Sgb, CreateValidHeader(), usingBootROM: false);

        var source = gpu.GetScreenData();
        for (var row = 0; row < 8; row++)
        {
            SetTransferWord(source, row, 0x00FF); // Tile zero is solid color index one.
        }

        var characterTransfer = new byte[16];
        characterTransfer[0] = 0x99; // CHR_TRN, low tile bank, one packet
        SendPacket(joypad, characterTransfer);
        CompleteTransfer(sgb);

        Array.Clear(source, 0, source.Length);
        SetTransferWord(source, 1025, 0x001F); // Border palette zero, color one = RGB555 red.
        var pictureTransfer = new byte[16];
        pictureTransfer[0] = 0xA1; // PCT_TRN, one packet
        SendPacket(joypad, pictureTransfer);
        CompleteTransfer(sgb);

        var pixel = sgb.GetScreenData()[0, 0];
        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    [Fact]
    public void FrontendExposesDistinctSgbAndSgb2Selections()
    {
        using var rom = CreateSgbRom();
        var sgb = FrontendOptions.Parse(new[] { rom.Path, "--sgb" });
        var sgb2 = FrontendOptions.Parse(new[] { rom.Path, "--sgb2" });

        Assert.True(sgb.ForceSGB);
        Assert.False(sgb.ForceSGB2);
        Assert.True(sgb2.ForceSGB2);
        Assert.False(sgb2.ForceSGB);
        Assert.Throws<ArgumentException>(() => FrontendOptions.Parse(new[] { rom.Path, "--dmg", "--sgb" }));
    }

    private static void SendPacket(Joypad joypad, byte[] packet)
    {
        joypad.WriteByte(0x00, MemorySchema.JOYPAD_REGISTER);
        joypad.WriteByte(0x30, MemorySchema.JOYPAD_REGISTER);
        foreach (var value in packet)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                joypad.WriteByte((value & (1 << bit)) == 0 ? (byte)0x20 : (byte)0x10, MemorySchema.JOYPAD_REGISTER);
                joypad.WriteByte(0x30, MemorySchema.JOYPAD_REGISTER);
            }
        }

        joypad.WriteByte(0x20, MemorySchema.JOYPAD_REGISTER);
        joypad.WriteByte(0x30, MemorySchema.JOYPAD_REGISTER);
    }

    private static void CompleteTransfer(SgbSystem sgb)
    {
        sgb.FrameCompleted();
        sgb.FrameCompleted();
        sgb.FrameCompleted();
    }

    private static void SetTransferWord(Color[,] screen, int wordIndex, ushort value)
    {
        var tile = wordIndex / 8;
        var row = wordIndex & 7;
        var startX = tile % 20 * 8;
        var y = tile / 20 * 8 + row;
        for (var x = 0; x < 8; x++)
        {
            var bit = 7 - x;
            screen[startX + x, y].SgbIndex = (byte)(((value >> bit) & 1) | ((value >> (bit + 8)) & 1) << 1);
        }
    }

    private static TestRom CreateSgbRom()
    {
        var rom = TestRom.Create(0x18, 0xFE);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x146] = 0x03;
        bytes[0x14B] = 0x33;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }

    private static byte[] CreateValidHeader()
    {
        var bytes = new byte[0x8000];
        bytes[0x146] = 0x03;
        bytes[0x14B] = 0x33;
        return bytes;
    }

    private static Emulator Start(TestRom rom, BootMode mode)
    {
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootMode = mode
        }));
        return emulator;
    }
}
