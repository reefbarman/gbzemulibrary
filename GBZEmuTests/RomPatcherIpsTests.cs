using System.Text;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies IPS format detection, record application, output sizing, input ownership, and malformed-data handling.
/// </summary>
public sealed class RomPatcherIpsTests
{
    [Fact]
    public void DetectFormatUsesPatchMagic()
    {
        Assert.Equal(RomPatchFormat.Ips, RomPatcher.DetectFormat(Encoding.ASCII.GetBytes("PATCHEOF")));
        Assert.Equal(RomPatchFormat.Bps, RomPatcher.DetectFormat(Encoding.ASCII.GetBytes("BPS1")));
        Assert.Throws<RomPatchException>(() => RomPatcher.DetectFormat(Encoding.ASCII.GetBytes("IPS")));
    }

    [Fact]
    public void ApplyWritesOrdinaryRecordWithoutMutatingInputs()
    {
        var source = new byte[] { 0, 1, 2, 3 };
        var patch = CreateIps(builder => builder.Record(1, 9, 8));
        var originalSource = (byte[])source.Clone();
        var originalPatch = (byte[])patch.Clone();

        var output = RomPatcher.Apply(source, patch);

        Assert.Equal(new byte[] { 0, 9, 8, 3 }, output);
        Assert.Equal(originalSource, source);
        Assert.Equal(originalPatch, patch);
        Assert.NotSame(source, output);
    }

    [Fact]
    public void ApplyWritesRleRecord()
    {
        var patch = CreateIps(builder => builder.Rle(2, 4, 0xA5));

        var output = RomPatcher.Apply(new byte[] { 1, 2, 3 }, patch);

        Assert.Equal(new byte[] { 1, 2, 0xA5, 0xA5, 0xA5, 0xA5 }, output);
    }

    [Fact]
    public void ApplyExpandsWithZeroFilledGap()
    {
        var patch = CreateIps(builder => builder.Record(4, 0x7E));

        var output = RomPatcher.Apply(new byte[] { 1, 2 }, patch);

        Assert.Equal(new byte[] { 1, 2, 0, 0, 0x7E }, output);
    }

    [Fact]
    public void ApplyUsesRecordOrderForOverlappingWrites()
    {
        var patch = CreateIps(builder =>
        {
            builder.Record(1, 1, 1, 1);
            builder.Record(2, 9, 8);
        });

        var output = RomPatcher.Apply(new byte[5], patch);

        Assert.Equal(new byte[] { 0, 1, 9, 8, 0 }, output);
    }

    [Fact]
    public void ApplyOptionalFinalSizeTruncatesOutput()
    {
        var patch = CreateIps(_ => { }, finalSize: 3);

        var output = RomPatcher.Apply(new byte[] { 1, 2, 3, 4, 5 }, patch);

        Assert.Equal(new byte[] { 1, 2, 3 }, output);
    }

    [Fact]
    public void ApplyOptionalFinalSizeExtendsWithZeros()
    {
        var patch = CreateIps(_ => { }, finalSize: 5);

        var output = RomPatcher.Apply(new byte[] { 1, 2 }, patch);

        Assert.Equal(new byte[] { 1, 2, 0, 0, 0 }, output);
    }

    [Fact]
    public void ApplyOptionalFinalSizeCanProduceEmptyTarget()
    {
        var patch = CreateIps(_ => { }, finalSize: 0);

        Assert.Empty(RomPatcher.Apply(new byte[] { 1, 2 }, patch));
    }

    [Theory]
    [MemberData(nameof(MalformedPatches))]
    public void ApplyRejectsMalformedIps(byte[] patch, string expectedMessage)
    {
        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(new byte[4], patch));

        Assert.Equal(RomPatchFormat.Ips, error.Format);
        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsRecordBeyondMaximumOutput()
    {
        var patch = CreateIps(builder => builder.Record(0x7FFFFF, 1, 2));

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(Array.Empty<byte>(), patch));

        Assert.Equal(RomPatchFormat.Ips, error.Format);
        Assert.Contains("8 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyRejectsOptionalFinalSizeBeyondMaximumOutput()
    {
        var patch = CreateIps(_ => { }, finalSize: 0x800001);

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(Array.Empty<byte>(), patch));

        Assert.Equal(RomPatchFormat.Ips, error.Format);
        Assert.Contains("8 MiB", error.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> MalformedPatches()
    {
        yield return new object[] { Encoding.ASCII.GetBytes("PATCH"), "EOF" };
        yield return new object[] { Bytes("PATCH", 0, 0, 0), "record size" };
        yield return new object[] { Bytes("PATCH", 0, 0, 0, 0, 1), "payload" };
        yield return new object[] { Bytes("PATCH", 0, 0, 0, 0, 0, 0, 0, 0x7F), "at least one" };
        yield return new object[] { Bytes("PATCHEOF", 0), "unsupported data" };
        yield return new object[] { Bytes("PATCHEOF", 0, 0), "unsupported data" };
        yield return new object[] { Bytes("PATCHEOF", 0, 0, 0, 0), "unsupported data" };
    }

    private static byte[] Bytes(string prefix, params byte[] suffix)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(prefix));
        bytes.AddRange(suffix);
        return bytes.ToArray();
    }

    private static byte[] CreateIps(Action<IpsBuilder> configure, int? finalSize = null)
    {
        var builder = new IpsBuilder();
        configure(builder);
        return builder.Build(finalSize);
    }

    private sealed class IpsBuilder
    {
        private readonly List<byte> _bytes = new(Encoding.ASCII.GetBytes("PATCH"));

        public void Record(int offset, params byte[] data)
        {
            WriteUInt24(offset);
            WriteUInt16(data.Length);
            _bytes.AddRange(data);
        }

        public void Rle(int offset, int length, byte value)
        {
            WriteUInt24(offset);
            WriteUInt16(0);
            WriteUInt16(length);
            _bytes.Add(value);
        }

        public byte[] Build(int? finalSize)
        {
            _bytes.AddRange(Encoding.ASCII.GetBytes("EOF"));
            if (finalSize.HasValue)
            {
                WriteUInt24(finalSize.Value);
            }

            return _bytes.ToArray();
        }

        private void WriteUInt24(int value)
        {
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        private void WriteUInt16(int value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }
    }
}
