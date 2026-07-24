using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GBZEmuFrontend;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies deterministic frontend ROM launch resolution without initializing the Raylib host.
/// </summary>
public sealed class RomLaunchResolverTests
{
    [Fact]
    public void ResolveUnpatchedRomUsesFileIdentityAndOwnedBytes()
    {
        using var fixture = new TemporaryFixture();
        var romBytes = CreateRomBytes();
        var romPath = fixture.WriteBytes("plain.gb", romBytes);
        var expectedHash = ComputeSha256(romBytes);

        var resolved = RomLaunchResolver.Resolve(romPath);

        Assert.Equal(Path.GetFullPath(romPath), resolved.BaseRomPath);
        Assert.Equal(romBytes, resolved.EffectiveBytes);
        Assert.NotSame(romBytes, resolved.EffectiveBytes);
        Assert.Equal(expectedHash, resolved.BaseSha256);
        Assert.Equal(expectedHash, resolved.EffectiveSha256);
        Assert.Equal("plain.gb", resolved.PersistenceIdentity);
        Assert.Equal("plain", resolved.DisplayName);
        Assert.Equal(2, resolved.CartridgeInspection.PhysicalRomBanks);
        Assert.Empty(resolved.AppliedPatches);
    }

    [Fact]
    public void ResolveSchemaV1ManifestAppliesIpsAndReportsHashesAndDisplayName()
    {
        using var fixture = new TemporaryFixture();
        var baseBytes = CreateRomBytes();
        var effectiveBytes = (byte[])baseBytes.Clone();
        effectiveBytes[0x150] = 0x42;
        var patchBytes = CreateIpsPatch(0x150, 0x42);
        var romPath = fixture.WriteBytes("patched.gb", baseBytes);
        fixture.WriteBytes("translation.ips", patchBytes);
        fixture.WriteManifest(
            "patched.gb.json",
            new
            {
                schemaVersion = 1,
                displayName = "Deterministic Translation",
                sourceSha256 = ComputeSha256(baseBytes),
                targetSha256 = ComputeSha256(effectiveBytes),
                patches = new[] { "translation.ips" }
            });

        var resolved = RomLaunchResolver.Resolve(romPath);

        Assert.Equal(effectiveBytes, resolved.EffectiveBytes);
        Assert.Equal(ComputeSha256(baseBytes), resolved.BaseSha256);
        Assert.Equal(ComputeSha256(effectiveBytes), resolved.EffectiveSha256);
        Assert.Equal("Deterministic Translation", resolved.DisplayName);
        Assert.Equal($"patched-{ComputeSha256(effectiveBytes)}", resolved.PersistenceIdentity);
        var appliedPatch = Assert.Single(resolved.AppliedPatches);
        Assert.Equal("translation.ips", appliedPatch.FileName);
        Assert.Equal(RomPatchFormat.Ips, appliedPatch.Format);
        Assert.Equal(ComputeSha256(patchBytes), appliedPatch.Sha256);
    }

    [Fact]
    public void ResolveAppliesManifestPatchesInArrayOrder()
    {
        using var fixture = new TemporaryFixture();
        var baseBytes = CreateRomBytes();
        var romPath = fixture.WriteBytes("ordered.gb", baseBytes);
        fixture.WriteBytes("first.ips", CreateIpsPatch(0x150, 0x11));
        fixture.WriteBytes("second.ips", CreateIpsPatch(0x150, 0x22));
        fixture.WriteManifest(
            "ordered.gb.json",
            new
            {
                schemaVersion = 1,
                patches = new[] { "first.ips", "second.ips" }
            });

        var resolved = RomLaunchResolver.Resolve(romPath);

        Assert.Equal(0x22, resolved.EffectiveBytes[0x150]);
        Assert.Collection(
            resolved.AppliedPatches,
            patch => Assert.Equal("first.ips", patch.FileName),
            patch => Assert.Equal("second.ips", patch.FileName));
    }

    [Fact]
    public void ResolveRomAutoDiscoversAdjacentManifest()
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("auto.gbc", CreateRomBytes());
        fixture.WriteManifest(
            "auto.gbc.json",
            new
            {
                schemaVersion = 1,
                displayName = "Adjacent Manifest"
            });

        var resolved = RomLaunchResolver.Resolve(romPath);

        Assert.Equal("Adjacent Manifest", resolved.DisplayName);
    }

    [Fact]
    public void ResolveAcceptsManifestAsExplicitLaunchTarget()
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("explicit.gb", CreateRomBytes());
        var manifestPath = fixture.WriteManifest(
            "explicit.gb.json",
            new
            {
                schemaVersion = 1,
                displayName = "Explicit Manifest"
            });

        var resolved = RomLaunchResolver.Resolve(manifestPath);

        Assert.Equal(Path.GetFullPath(romPath), resolved.BaseRomPath);
        Assert.Equal("Explicit Manifest", resolved.DisplayName);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":1,\"displayName\":42}")]
    [InlineData("{\"schemaVersion\":1,\"patches\":\"change.ips\"}")]
    [InlineData("{\"schemaVersion\":1,\"patches\":[42]}")]
    [InlineData("{\"schemaVersion\":1,\"cartridge\":\"dmg\"}")]
    public void ResolveRejectsMalformedOrTypeInvalidManifest(string manifestJson)
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("invalid.gb", CreateRomBytes());
        fixture.WriteText("invalid.gb.json", manifestJson);

        Assert.Throws<InvalidDataException>(() => RomLaunchResolver.Resolve(romPath));
    }

    [Theory]
    [InlineData("../change.ips")]
    [InlineData("subdirectory/change.ips")]
    [InlineData("subdirectory\\change.ips")]
    [InlineData("/absolute/change.ips")]
    public void ResolveRejectsUnsafePatchNames(string patchName)
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("unsafe.gb", CreateRomBytes());
        fixture.WriteManifest(
            "unsafe.gb.json",
            new
            {
                schemaVersion = 1,
                patches = new[] { patchName }
            });

        var error = Assert.Throws<InvalidDataException>(() => RomLaunchResolver.Resolve(romPath));

        Assert.Contains("sibling leaf filenames", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsPatchExtensionMagicMismatch()
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("mismatch.gb", CreateRomBytes());
        fixture.WriteBytes("change.bps", CreateIpsPatch(0x150, 0x42));
        fixture.WriteManifest(
            "mismatch.gb.json",
            new
            {
                schemaVersion = 1,
                patches = new[] { "change.bps" }
            });

        var error = Assert.Throws<InvalidDataException>(() => RomLaunchResolver.Resolve(romPath));

        Assert.Contains("does not match detected IPS format", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsSourceHashMismatch()
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("source.gb", CreateRomBytes());
        fixture.WriteManifest(
            "source.gb.json",
            new
            {
                schemaVersion = 1,
                sourceSha256 = new string('0', 64)
            });

        var error = Assert.Throws<InvalidDataException>(() => RomLaunchResolver.Resolve(romPath));

        Assert.Contains("sourceSha256 mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsTargetHashMismatch()
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("target.gb", CreateRomBytes());
        fixture.WriteManifest(
            "target.gb.json",
            new
            {
                schemaVersion = 1,
                targetSha256 = new string('f', 64)
            });

        var error = Assert.Throws<InvalidDataException>(() => RomLaunchResolver.Resolve(romPath));

        Assert.Contains("targetSha256 mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsMoreThanThirtyTwoPatches()
    {
        using var fixture = new TemporaryFixture();
        var romPath = fixture.WriteBytes("many.gb", CreateRomBytes());
        fixture.WriteManifest(
            "many.gb.json",
            new
            {
                schemaVersion = 1,
                patches = Enumerable.Range(0, 33).Select(index => $"patch-{index}.ips").ToArray()
            });

        var error = Assert.Throws<InvalidDataException>(() => RomLaunchResolver.Resolve(romPath));

        Assert.Contains("at most 32 patches", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchedIdentityDependsOnEffectiveBytesNotManifestOrBaseFileName()
    {
        using var first = new TemporaryFixture();
        using var second = new TemporaryFixture();
        var baseBytes = CreateRomBytes();
        var patchBytes = CreateIpsPatch(0x150, 0xA5);
        var firstRomPath = WritePatchedFixture(first, "first.gb", "first.ips", baseBytes, patchBytes);
        var secondRomPath = WritePatchedFixture(second, "second.gbc", "second.ips", baseBytes, patchBytes);

        var firstResolved = RomLaunchResolver.Resolve(firstRomPath);
        var secondResolved = RomLaunchResolver.Resolve(secondRomPath);

        Assert.Equal(firstResolved.EffectiveSha256, secondResolved.EffectiveSha256);
        Assert.Equal(firstResolved.PersistenceIdentity, secondResolved.PersistenceIdentity);
        Assert.Equal($"patched-{firstResolved.EffectiveSha256}", firstResolved.PersistenceIdentity);
        Assert.NotEqual(Path.GetFileName(firstRomPath), firstResolved.PersistenceIdentity);
        Assert.NotEqual(Path.GetFileName(secondRomPath), secondResolved.PersistenceIdentity);
    }

    private static string WritePatchedFixture(
        TemporaryFixture fixture,
        string romFileName,
        string patchFileName,
        byte[] romBytes,
        byte[] patchBytes)
    {
        var romPath = fixture.WriteBytes(romFileName, romBytes);
        fixture.WriteBytes(patchFileName, patchBytes);
        fixture.WriteManifest(
            $"{romFileName}.json",
            new
            {
                schemaVersion = 1,
                patches = new[] { patchFileName }
            });
        return romPath;
    }

    private static byte[] CreateRomBytes()
    {
        var bytes = new byte[0x8000];
        bytes[0x147] = 0x00;
        bytes[0x148] = 0x00;
        bytes[0x149] = 0x00;
        return bytes;
    }

    private static byte[] CreateIpsPatch(int offset, params byte[] data)
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

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class TemporaryFixture : IDisposable
    {
        public TemporaryFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"gbzemu-launch-resolver-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string WriteBytes(string fileName, byte[] bytes)
        {
            var path = Path.Combine(DirectoryPath, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string WriteManifest(string fileName, object manifest)
        {
            return WriteText(fileName, JsonSerializer.Serialize(manifest));
        }

        public string WriteText(string fileName, string text)
        {
            var path = Path.Combine(DirectoryPath, fileName);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
