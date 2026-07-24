using System.Text;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies BPS actions, relative cursors, checksums, metadata, ownership, and malformed-data handling.
/// </summary>
public sealed class RomPatcherBpsTests
{
    [Fact]
    public void ApplyMatchesIndependentKnownAnswerFixture()
    {
        var source = new byte[] { 1, 2, 3 };
        var patch = new byte[]
        {
            0x42, 0x50, 0x53, 0x31,
            0x83, 0x83, 0x80,
            0x89, 0x01, 0x09, 0x03,
            0x1D, 0x80, 0xBC, 0x55,
            0xD6, 0x59, 0x48, 0xB6,
            0x90, 0xE9, 0x3E, 0x40
        };

        Assert.Equal(new byte[] { 1, 9, 3 }, RomPatcher.Apply(source, patch));
    }

    [Fact]
    public void ApplyExecutesAllActionsAndOverlappingTargetCopy()
    {
        var source = new byte[] { 10, 11, 12, 13, 14, 15 };
        var target = new byte[] { 10, 11, 99, 13, 14, 99, 13, 14, 99 };
        var patch = BuildPatch(source, target, builder =>
        {
            builder.SourceRead(2);
            builder.TargetRead(99);
            builder.SourceCopy(3, 2);
            builder.TargetCopy(2, 4);
        });
        var originalSource = (byte[])source.Clone();
        var originalPatch = (byte[])patch.Clone();

        var output = RomPatcher.Apply(source, patch);

        Assert.Equal(target, output);
        Assert.Equal(originalSource, source);
        Assert.Equal(originalPatch, patch);
    }

    [Fact]
    public void ApplySupportsNegativeSourceRelativeOffset()
    {
        var source = new byte[] { 1, 2, 3, 4, 5, 6 };
        var target = new byte[] { 5, 2 };
        var patch = BuildPatch(source, target, builder =>
        {
            builder.SourceCopy(4, 1);
            builder.SourceCopy(-4, 1);
        });

        Assert.Equal(target, RomPatcher.Apply(source, patch));
    }

    [Fact]
    public void ApplySupportsNegativeTargetRelativeOffset()
    {
        var source = Array.Empty<byte>();
        var target = new byte[] { 1, 2, 1, 1 };
        var patch = BuildPatch(source, target, builder =>
        {
            builder.TargetRead(1, 2);
            builder.TargetCopy(0, 1);
            builder.TargetCopy(-1, 1);
        });

        Assert.Equal(target, RomPatcher.Apply(source, patch));
    }

    [Fact]
    public void ApplySkipsMetadata()
    {
        var source = new byte[] { 4, 5 };
        var target = new byte[] { 4, 5 };
        var patch = BuildPatch(source, target, builder => builder.SourceRead(2), Encoding.UTF8.GetBytes("<meta/>"));

        Assert.Equal(target, RomPatcher.Apply(source, patch));
    }

    [Fact]
    public void ApplySupportsEmptyTargetWithoutActions()
    {
        var source = Array.Empty<byte>();
        var patch = BuildPatch(source, Array.Empty<byte>(), _ => { });

        var output = RomPatcher.Apply(source, patch);

        Assert.Empty(output);
        Assert.NotSame(source, output);
    }

    [Fact]
    public void Crc32MatchesStandardKnownAnswer()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789"), 0, 9));
    }

    [Fact]
    public void ApplyRejectsPatchChecksumBeforeParsingActions()
    {
        var source = new byte[] { 1 };
        var patch = BuildPatch(source, source, builder => builder.SourceRead(1));
        patch[4] ^= 0x01;

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Equal(RomPatchFormat.Bps, error.Format);
        Assert.Contains("patch checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsSourceSizeMismatch()
    {
        var source = new byte[] { 1 };
        var patch = BuildPatch(new byte[] { 1, 2 }, new byte[] { 1 }, builder => builder.SourceRead(1));

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Contains("source size mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsSourceChecksumMismatch()
    {
        var source = new byte[] { 1, 2 };
        var patch = BuildPatch(source, source, builder => builder.SourceRead(2));
        patch[^12] ^= 0x01;
        RewritePatchChecksum(patch);

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Contains("source checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsTargetChecksumMismatch()
    {
        var source = new byte[] { 1, 2 };
        var patch = BuildPatch(source, source, builder => builder.SourceRead(2));
        patch[^8] ^= 0x01;
        RewritePatchChecksum(patch);

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Contains("target checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsTargetBeyondMaximumOutputBeforeAllocation()
    {
        var source = Array.Empty<byte>();
        var patch = BuildRawPatch(
            source,
            Array.Empty<byte>(),
            body =>
            {
                WriteNumber(body, 0);
                WriteNumber(body, 0x800001);
                WriteNumber(body, 0);
            });

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Contains("8 MiB", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidActionPatches))]
    public void ApplyRejectsInvalidActionRanges(byte[] source, byte[] patch, string expectedMessage)
    {
        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Equal(RomPatchFormat.Bps, error.Format);
        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsUnterminatedVariableInteger()
    {
        var source = Array.Empty<byte>();
        var patch = BuildRawPatch(source, Array.Empty<byte>(), body =>
        {
            for (var index = 0; index < 12; index++)
            {
                body.Add(0x7F);
            }
        });

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Contains("source size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRejectsTargetReadThatConsumesChecksumFooter()
    {
        var source = Array.Empty<byte>();
        var patch = BuildRawPatch(source, new byte[] { 0 }, body =>
        {
            WriteNumber(body, 0);
            WriteNumber(body, 1);
            WriteNumber(body, 0);
            WriteNumber(body, 1);
        });

        var error = Assert.Throws<RomPatchException>(() => RomPatcher.Apply(source, patch));

        Assert.Contains("TargetRead", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checksum footer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidActionPatches()
    {
        var sourceReadSource = new byte[] { 1 };
        yield return new object[]
        {
            sourceReadSource,
            BuildPatch(sourceReadSource, new byte[2], builder => builder.SourceRead(2)),
            "SourceRead"
        };

        var sourceCopySource = new byte[] { 1 };
        yield return new object[]
        {
            sourceCopySource,
            BuildPatch(sourceCopySource, new byte[1], builder => builder.SourceCopy(1, 1)),
            "SourceCopy"
        };

        yield return new object[]
        {
            Array.Empty<byte>(),
            BuildPatch(Array.Empty<byte>(), new byte[1], builder => builder.TargetCopy(0, 1)),
            "TargetCopy"
        };

        yield return new object[]
        {
            new byte[] { 1 },
            BuildPatch(new byte[] { 1 }, new byte[1], builder => builder.SourceRead(2)),
            "declared target size"
        };

        yield return new object[]
        {
            new byte[] { 1 },
            BuildPatch(new byte[] { 1 }, new byte[] { 1 }, builder =>
            {
                builder.SourceRead(1);
                builder.TargetRead(2);
            }),
            "does not end"
        };
    }

    private static byte[] BuildPatch(
        byte[] source,
        byte[] target,
        Action<BpsBuilder> configure,
        byte[]? metadata = null)
    {
        var builder = new BpsBuilder(source.Length, target.Length, metadata ?? Array.Empty<byte>());
        configure(builder);
        return builder.Build(source, target);
    }

    private static byte[] BuildRawPatch(byte[] source, byte[] target, Action<List<byte>> writeBody)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("BPS1"));
        writeBody(body);
        AppendFooter(body, source, target);
        return body.ToArray();
    }

    private static void AppendFooter(List<byte> bytes, byte[] source, byte[] target)
    {
        WriteUInt32LittleEndian(bytes, ComputeCrc32(source));
        WriteUInt32LittleEndian(bytes, ComputeCrc32(target));
        WriteUInt32LittleEndian(bytes, ComputeCrc32(bytes.ToArray()));
    }

    private static void RewritePatchChecksum(byte[] patch)
    {
        var checksum = ComputeCrc32(patch.AsSpan(0, patch.Length - 4).ToArray());
        patch[^4] = (byte)checksum;
        patch[^3] = (byte)(checksum >> 8);
        patch[^2] = (byte)(checksum >> 16);
        patch[^1] = (byte)(checksum >> 24);
    }

    private static void WriteNumber(List<byte> bytes, ulong data)
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

    private static uint ComputeCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        for (var index = 0; index < data.Length; index++)
        {
            crc ^= data[index];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private sealed class BpsBuilder
    {
        private readonly List<byte> _bytes = new(Encoding.ASCII.GetBytes("BPS1"));

        public BpsBuilder(int sourceSize, int targetSize, byte[] metadata)
        {
            WriteNumber(_bytes, (ulong)sourceSize);
            WriteNumber(_bytes, (ulong)targetSize);
            WriteNumber(_bytes, (ulong)metadata.Length);
            _bytes.AddRange(metadata);
        }

        public void SourceRead(int length)
        {
            WriteAction(0, length);
        }

        public void TargetRead(params byte[] data)
        {
            WriteAction(1, data.Length);
            _bytes.AddRange(data);
        }

        public void SourceCopy(long relativeOffset, int length)
        {
            WriteAction(2, length);
            WriteSignedOffset(relativeOffset);
        }

        public void TargetCopy(long relativeOffset, int length)
        {
            WriteAction(3, length);
            WriteSignedOffset(relativeOffset);
        }

        public byte[] Build(byte[] source, byte[] target)
        {
            AppendFooter(_bytes, source, target);
            return _bytes.ToArray();
        }

        private void WriteAction(int action, int length)
        {
            WriteNumber(_bytes, ((ulong)(length - 1) << 2) | (uint)action);
        }

        private void WriteSignedOffset(long relativeOffset)
        {
            var magnitude = (ulong)Math.Abs(relativeOffset);
            WriteNumber(_bytes, (magnitude << 1) | (relativeOffset < 0 ? 1u : 0u));
        }
    }
}
