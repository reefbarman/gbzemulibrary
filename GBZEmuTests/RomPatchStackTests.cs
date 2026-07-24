using System.Text;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies ordered ROM-patch composition and contextual stack failures.
/// </summary>
public sealed class RomPatchStackTests
{
    [Fact]
    public void ApplyStackUsesEachOutputAsNextSource()
    {
        var source = new byte[] { 0, 0, 0 };
        var first = CreateSingleRecordIps(0, 1, 2);
        var second = CreateSingleRecordIps(1, 9, 8);
        var original = (byte[])source.Clone();

        var output = RomPatcher.Apply(source, new[] { first, second });

        Assert.Equal(new byte[] { 1, 9, 8 }, output);
        Assert.Equal(original, source);
    }

    [Fact]
    public void ApplyStackComposesIpsThenBps()
    {
        var source = new byte[] { 0, 0, 0 };
        var ips = CreateSingleRecordIps(0, 1, 2, 3);
        var bps = CreateTargetReadBps(new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3 });

        var output = RomPatcher.Apply(source, new[] { ips, bps });

        Assert.Equal(new byte[] { 1, 9, 3 }, output);
    }

    [Fact]
    public void ApplyStackComposesBpsThenIps()
    {
        var source = new byte[] { 0, 0, 0 };
        var bps = CreateTargetReadBps(source, new byte[] { 4, 5, 6 });
        var ips = CreateSingleRecordIps(1, 8);

        var output = RomPatcher.Apply(source, new[] { bps, ips });

        Assert.Equal(new byte[] { 4, 8, 6 }, output);
    }

    [Fact]
    public void ApplyEmptyStackReturnsPrivateCopy()
    {
        var source = new byte[] { 1, 2, 3 };

        var output = RomPatcher.Apply(source, Array.Empty<byte[]>());

        Assert.Equal(source, output);
        Assert.NotSame(source, output);
    }

    [Fact]
    public void ApplyStackReportsOneBasedFailingPatchIndexAndFormat()
    {
        var valid = CreateSingleRecordIps(0, 1);
        var invalid = Encoding.ASCII.GetBytes("PATCH");

        var error = Assert.Throws<RomPatchException>(() =>
            RomPatcher.Apply(new byte[] { 0 }, new[] { valid, invalid }));

        Assert.Equal(2, error.PatchIndex);
        Assert.Equal(RomPatchFormat.Ips, error.Format);
        Assert.Contains("Patch 2 failed", error.Message, StringComparison.Ordinal);
        Assert.IsType<RomPatchException>(error.InnerException);
    }

    [Fact]
    public void ApplyStackReportsBpsFailureIndexAndFormat()
    {
        var source = new byte[] { 0, 0, 0 };
        var first = CreateSingleRecordIps(0, 1, 2, 3);
        var invalid = CreateTargetReadBps(new byte[] { 9, 9, 9 }, new byte[] { 4, 5, 6 });

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, new[] { first, invalid }));

        Assert.Equal(2, error.PatchIndex);
        Assert.Equal(RomPatchFormat.Bps, error.Format);
        Assert.Contains("Patch 2 failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("source checksum mismatch", error.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyStackReportsNullPatchIndex()
    {
        var patches = new byte[][] { CreateSingleRecordIps(0, 1), null! };

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(new byte[] { 0 }, patches));

        Assert.Equal(2, error.PatchIndex);
        Assert.Null(error.Format);
        Assert.Contains("Patch 2 was null", error.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateSingleRecordIps(int offset, params byte[] data)
    {
        var patch = new List<byte>(Encoding.ASCII.GetBytes("PATCH"))
        {
            (byte)(offset >> 16),
            (byte)(offset >> 8),
            (byte)offset,
            (byte)(data.Length >> 8),
            (byte)data.Length
        };
        patch.AddRange(data);
        patch.AddRange(Encoding.ASCII.GetBytes("EOF"));
        return patch.ToArray();
    }

    private static byte[] CreateTargetReadBps(byte[] source, byte[] target)
    {
        var patch = new List<byte>(Encoding.ASCII.GetBytes("BPS1"));
        WriteBpsNumber(patch, (ulong)source.Length);
        WriteBpsNumber(patch, (ulong)target.Length);
        WriteBpsNumber(patch, 0);
        WriteBpsNumber(patch, ((ulong)(target.Length - 1) << 2) | 1);
        patch.AddRange(target);
        WriteUInt32LittleEndian(patch, ComputeCrc32(source));
        WriteUInt32LittleEndian(patch, ComputeCrc32(target));
        WriteUInt32LittleEndian(patch, ComputeCrc32(patch));
        return patch.ToArray();
    }

    private static void WriteBpsNumber(List<byte> bytes, ulong data)
    {
        while (true)
        {
            var value = (byte)(data & 0x7F);
            data >>= 7;
            if (data == 0)
            {
                bytes.Add((byte)(0x80 | value));
                return;
            }

            bytes.Add(value);
            data--;
        }
    }

    private static void WriteUInt32LittleEndian(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }

    private static uint ComputeCrc32(IEnumerable<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
