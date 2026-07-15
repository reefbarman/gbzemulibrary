using System.Text;
using GBZEmuLibrary;

namespace GBZEmuTests;

internal sealed class RomTestRunner : IDisposable
{
    private readonly StringBuilder _serialOutput = new();
    private readonly string _saveDirectory;

    public Emulator Emulator { get; }
    public string SerialOutput => _serialOutput.ToString();

    public RomTestRunner(string romPath, BootMode bootMode = BootMode.DMG | BootMode.Skip)
    {
        _saveDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-saves-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_saveDirectory);

        Emulator = new Emulator();
        Emulator.Debug.SerialByteTransferred += value => _serialOutput.Append((char)value);

        var started = Emulator.Start(new Emulator.Config
        {
            ROMPath = romPath,
            SaveLocation = _saveDirectory,
            BootMode = bootMode
        });

        if (!started)
        {
            throw new InvalidOperationException($"Failed to load test ROM: {romPath}");
        }
    }

    public string RunSerialProtocol(int maxFrames)
    {
        for (var frame = 0; frame < maxFrames; frame++)
        {
            Emulator.Update();
            var output = SerialOutput;

            if (output.Contains("Failed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Serial result: {output.Trim()}");
            }

            if (output.Contains("Passed", StringComparison.Ordinal))
            {
                return output;
            }
        }

        throw new TimeoutException($"ROM did not report a serial result within {maxFrames} frames. Output: {SerialOutput}");
    }

    public BlarggMemoryResult RunBlarggMemoryProtocol(int maxFrames)
    {
        for (var frame = 0; frame < maxFrames; frame++)
        {
            Emulator.Update();

            if (!HasBlarggMemorySignature())
            {
                continue;
            }

            var status = Emulator.Debug.PeekByte(0xA000);
            if (status != 0x80)
            {
                return new BlarggMemoryResult(status, ReadNullTerminatedAscii(0xA004));
            }
        }

        throw new TimeoutException($"ROM did not report a Blargg memory result within {maxFrames} frames.");
    }

    public void RunToLoadBB(int maxFrames)
    {
        var reachedBreakpoint = false;
        Emulator.Debug.LoadBBExecuted += OnLoadBB;

        try
        {
            for (var frame = 0; frame < maxFrames && !reachedBreakpoint; frame++)
            {
                Emulator.Update();
            }
        }
        finally
        {
            Emulator.Debug.LoadBBExecuted -= OnLoadBB;
        }

        if (!reachedBreakpoint)
        {
            throw new TimeoutException($"ROM did not reach an LD B,B breakpoint within {maxFrames} frames.");
        }

        void OnLoadBB()
        {
            reachedBreakpoint = true;
            Emulator.Debug.RequestStop();
        }
    }

    public CpuDebugState RunMooneyeProtocol(int maxFrames)
    {
        CpuDebugState? result = null;
        Emulator.Debug.LoadBBExecuted += OnLoadBB;

        try
        {
            for (var frame = 0; frame < maxFrames && !result.HasValue; frame++)
            {
                Emulator.Update();
            }
        }
        finally
        {
            Emulator.Debug.LoadBBExecuted -= OnLoadBB;
        }

        if (!result.HasValue)
        {
            throw new TimeoutException($"ROM did not reach a Mooneye breakpoint within {maxFrames} frames.");
        }

        var state = result.Value;
        if ((byte)(state.BC >> 8) != 3 ||
            (byte)state.BC != 5 ||
            (byte)(state.DE >> 8) != 8 ||
            (byte)state.DE != 13 ||
            (byte)(state.HL >> 8) != 21 ||
            (byte)state.HL != 34)
        {
            throw new InvalidOperationException(
                $"Fibonacci fingerprint mismatch: B={(byte)(state.BC >> 8):X2}, C={(byte)state.BC:X2}, D={(byte)(state.DE >> 8):X2}, E={(byte)state.DE:X2}, H={(byte)(state.HL >> 8):X2}, L={(byte)state.HL:X2}.");
        }

        return state;

        void OnLoadBB()
        {
            result = Emulator.Debug.GetCpuState();
            Emulator.Debug.RequestStop();
        }
    }

    public void Dispose()
    {
        Emulator.Terminate();
        Directory.Delete(_saveDirectory, true);
    }

    private bool HasBlarggMemorySignature()
    {
        return Emulator.Debug.PeekByte(0xA001) == 0xDE &&
               Emulator.Debug.PeekByte(0xA002) == 0xB0 &&
               Emulator.Debug.PeekByte(0xA003) == 0x61;
    }

    private string ReadNullTerminatedAscii(int address)
    {
        var output = new StringBuilder();

        for (var i = 0; i < 1024; i++)
        {
            var value = Emulator.Debug.PeekByte(address + i);
            if (value == 0)
            {
                return output.ToString();
            }

            output.Append((char)value);
        }

        return output.ToString();
    }
}

internal readonly record struct BlarggMemoryResult(byte Status, string Message)
{
    public bool Passed => Status == 0;
}
