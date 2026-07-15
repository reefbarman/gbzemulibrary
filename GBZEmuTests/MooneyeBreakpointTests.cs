using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class MooneyeBreakpointTests
{
    /// <summary>
    /// Requests a stop from the Mooneye LD B,B hook and verifies the instruction has completed without executing
    /// the following opcode. This guarantees conformance snapshots represent the ROM's exact completion point.
    /// </summary>
    [Fact]
    public void LoadBBHookCanStopAtExactCompletionPoint()
    {
        using var rom = TestRom.Create(0x40, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;

        emulator.Update();

        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(0x0101, emulator.Debug.GetCpuState().PC);
        Assert.Equal((ulong)1, emulator.Debug.GetCpuState().ExecutedInstructionCount);
        emulator.Terminate();
    }
}
