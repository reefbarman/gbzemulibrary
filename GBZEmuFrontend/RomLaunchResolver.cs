using System.Security.Cryptography;
using System.Text.Json;
using GBZEmuLibrary;

namespace GBZEmuFrontend;

/// <summary>
/// Describes one patch applied while resolving a frontend ROM launch.
/// </summary>
internal sealed class AppliedRomPatch
{
    public AppliedRomPatch(string fileName, RomPatchFormat format, string sha256)
    {
        FileName = fileName;
        Format = format;
        Sha256 = sha256;
    }

    public string FileName { get; }
    public RomPatchFormat Format { get; }
    public string Sha256 { get; }
}

/// <summary>
/// Owns the final ROM bytes and identities used by every downstream frontend launch policy.
/// </summary>
internal sealed class ResolvedRomImage
{
    public ResolvedRomImage(
        string baseRomPath,
        byte[] effectiveBytes,
        string baseSha256,
        string effectiveSha256,
        string persistenceIdentity,
        string displayName,
        CartridgeInspection cartridgeInspection,
        IReadOnlyList<AppliedRomPatch> appliedPatches)
    {
        BaseRomPath = baseRomPath;
        EffectiveBytes = effectiveBytes;
        BaseSha256 = baseSha256;
        EffectiveSha256 = effectiveSha256;
        PersistenceIdentity = persistenceIdentity;
        DisplayName = displayName;
        CartridgeInspection = cartridgeInspection;
        AppliedPatches = appliedPatches;
    }

    public string BaseRomPath { get; }
    public byte[] EffectiveBytes { get; }
    public string BaseSha256 { get; }
    public string EffectiveSha256 { get; }
    public string PersistenceIdentity { get; }
    public string DisplayName { get; }
    public CartridgeInspection CartridgeInspection { get; }
    public IReadOnlyList<AppliedRomPatch> AppliedPatches { get; }
}

/// <summary>
/// Resolves a ROM or adjacent schema-v1 manifest into validated, privately owned effective bytes.
/// </summary>
internal static class RomLaunchResolver
{
    private const int MaximumManifestSize = 64 * 1024;
    private const int MaximumPatchCount = 32;
    private const int MaximumPatchSize = 64 * 1024 * 1024;
    private const int MaximumRomSize = 8 * 1024 * 1024;

    public static ResolvedRomImage Resolve(string launchTargetPath)
    {
        if (string.IsNullOrWhiteSpace(launchTargetPath))
        {
            throw new ArgumentException("A ROM or manifest path is required.", nameof(launchTargetPath));
        }

        var fullTargetPath = Path.GetFullPath(launchTargetPath);
        var explicitManifest = IsManifestPath(fullTargetPath);
        string baseRomPath;
        string manifestPath;
        if (explicitManifest)
        {
            manifestPath = fullTargetPath;
            baseRomPath = fullTargetPath[..^".json".Length];
        }
        else
        {
            if (!IsRomPath(fullTargetPath))
            {
                throw new ArgumentException("The launch target must end in .gb, .gbc, .gb.json, or .gbc.json.");
            }

            baseRomPath = fullTargetPath;
            manifestPath = $"{baseRomPath}.json";
        }

        if (!File.Exists(baseRomPath))
        {
            throw new FileNotFoundException("Base ROM file not found.", baseRomPath);
        }

        if (explicitManifest && !File.Exists(manifestPath))
        {
            throw new FileNotFoundException("ROM launch manifest not found.", manifestPath);
        }

        var baseBytes = ReadBoundedFile(
            baseRomPath,
            MaximumRomSize,
            "The base ROM exceeds the supported 8 MiB cartridge limit.");
        var baseSha256 = ComputeSha256(baseBytes);
        var manifest = File.Exists(manifestPath)
            ? ReadManifest(manifestPath)
            : LaunchManifest.Empty;
        if (manifest.SourceSha256 != null && !HashesEqual(manifest.SourceSha256, baseSha256))
        {
            throw new InvalidDataException(
                $"Manifest sourceSha256 mismatch: expected {manifest.SourceSha256.ToLowerInvariant()}, actual {baseSha256}.");
        }

        var effectiveBytes = baseBytes;
        var appliedPatches = new List<AppliedRomPatch>(manifest.Patches.Count);
        var baseDirectory = Path.GetDirectoryName(baseRomPath)!;
        for (var index = 0; index < manifest.Patches.Count; index++)
        {
            var patchName = manifest.Patches[index];
            var patchPath = Path.Combine(baseDirectory, patchName);
            if (!File.Exists(patchPath))
            {
                throw new FileNotFoundException($"Patch {index + 1} not found: {patchName}", patchPath);
            }

            byte[] patchBytes;
            RomPatchFormat format;
            try
            {
                patchBytes = ReadBoundedFile(
                    patchPath,
                    MaximumPatchSize,
                    "The patch exceeds the supported 64 MiB limit.");
                format = RomPatcher.DetectFormat(patchBytes);
                ValidateExtensionMatchesFormat(patchName, format);
                effectiveBytes = RomPatcher.Apply(effectiveBytes, patchBytes);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                throw new InvalidDataException($"Patch {index + 1} ({patchName}) failed: {exception.Message}", exception);
            }

            appliedPatches.Add(new AppliedRomPatch(patchName, format, ComputeSha256(patchBytes)));
        }

        var effectiveSha256 = ComputeSha256(effectiveBytes);
        if (manifest.TargetSha256 != null && !HashesEqual(manifest.TargetSha256, effectiveSha256))
        {
            throw new InvalidDataException(
                $"Manifest targetSha256 mismatch: expected {manifest.TargetSha256.ToLowerInvariant()}, actual {effectiveSha256}.");
        }

        CartridgeInspection inspection;
        try
        {
            inspection = CartridgeInspection.Inspect(effectiveBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException($"The effective ROM is not a supported cartridge: {exception.Message}", exception);
        }

        var persistenceIdentity = appliedPatches.Count == 0
            ? Path.GetFileName(baseRomPath)
            : $"patched-{effectiveSha256}";
        var displayName = manifest.DisplayName ?? Path.GetFileNameWithoutExtension(baseRomPath);
        return new ResolvedRomImage(
            baseRomPath,
            effectiveBytes,
            baseSha256,
            effectiveSha256,
            persistenceIdentity,
            displayName,
            inspection,
            appliedPatches.AsReadOnly());
    }

    private static LaunchManifest ReadManifest(string manifestPath)
    {
        try
        {
            var manifestBytes = ReadBoundedFile(
                manifestPath,
                MaximumManifestSize,
                "The ROM launch manifest exceeds the supported 64 KiB limit.");
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The ROM launch manifest root must be an object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out var version) ||
                version != 1)
            {
                throw new InvalidDataException("The ROM launch manifest requires numeric schemaVersion 1.");
            }

            var displayName = ReadOptionalString(root, "displayName", requireHash: false);
            if (displayName != null && string.IsNullOrWhiteSpace(displayName))
            {
                throw new InvalidDataException("Manifest displayName must not be empty.");
            }

            var sourceSha256 = ReadOptionalString(root, "sourceSha256", requireHash: true);
            var targetSha256 = ReadOptionalString(root, "targetSha256", requireHash: true);
            var patches = ReadPatches(root);
            ValidateCartridge(root);

            return new LaunchManifest(displayName, sourceSha256, targetSha256, patches);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The ROM launch manifest is malformed JSON: {exception.Message}", exception);
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName, bool requireHash)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Manifest {propertyName} must be a string when supplied.");
        }

        var text = value.GetString()!;
        if (requireHash && !IsSha256(text))
        {
            throw new InvalidDataException($"Manifest {propertyName} must be a 64-character hexadecimal SHA-256 digest.");
        }

        return text;
    }

    private static IReadOnlyList<string> ReadPatches(JsonElement root)
    {
        if (!root.TryGetProperty("patches", out var value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Manifest patches must be an array when supplied.");
        }

        if (value.GetArrayLength() > MaximumPatchCount)
        {
            throw new InvalidDataException($"A ROM launch manifest may contain at most {MaximumPatchCount} patches.");
        }

        var patches = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Every manifest patch entry must be a string.");
            }

            var patchName = item.GetString()!;
            ValidateSiblingPatchName(patchName);
            patches.Add(patchName);
        }

        return patches.AsReadOnly();
    }

    private static void ValidateCartridge(JsonElement root)
    {
        if (!root.TryGetProperty("cartridge", out var cartridge))
        {
            return;
        }

        if (cartridge.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Manifest cartridge must be an object when supplied.");
        }

        ReadCartridgeString(cartridge, "style");
        var label = ReadCartridgeString(cartridge, "label");
        if (label != null &&
            (Path.IsPathRooted(label) || label.Contains('/') || label.Contains('\\') ||
             !string.Equals(Path.GetFileName(label), label, StringComparison.Ordinal) ||
             !Path.GetExtension(label).Equals(".png", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Manifest cartridge label must be a sibling .png filename.");
        }
    }

    private static string? ReadCartridgeString(JsonElement cartridge, string propertyName)
    {
        if (!cartridge.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Manifest cartridge {propertyName} must be a non-empty string when supplied.");
        }

        return value.GetString();
    }

    private static void ValidateSiblingPatchName(string patchName)
    {
        if (string.IsNullOrWhiteSpace(patchName) ||
            Path.IsPathRooted(patchName) ||
            patchName is "." or ".." ||
            patchName.Contains('/') ||
            patchName.Contains('\\') ||
            patchName.IndexOfAny("<>:\"|?*".ToCharArray()) >= 0 ||
            !string.Equals(Path.GetFileName(patchName), patchName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest patch paths must be portable sibling leaf filenames.");
        }

        var extension = Path.GetExtension(patchName);
        if (!extension.Equals(".ips", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bps", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest patch filenames must end in .ips or .bps.");
        }
    }

    private static void ValidateExtensionMatchesFormat(string patchName, RomPatchFormat format)
    {
        var extension = Path.GetExtension(patchName);
        var matches = format == RomPatchFormat.Ips
            ? extension.Equals(".ips", StringComparison.OrdinalIgnoreCase)
            : extension.Equals(".bps", StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            throw new InvalidDataException(
                $"Patch extension {extension} does not match detected {format.ToString().ToUpperInvariant()} format.");
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumLength, string oversizedMessage)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximumLength)
        {
            throw new InvalidDataException(oversizedMessage);
        }

        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"File changed while it was being read: {Path.GetFileName(path)}");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(oversizedMessage);
        }

        return bytes;
    }

    private static bool IsManifestPath(string path)
    {
        return path.EndsWith(".gb.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gbc.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRomPath(string path)
    {
        return path.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HashesEqual(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class LaunchManifest
    {
        public static LaunchManifest Empty { get; } = new(null, null, null, Array.Empty<string>());

        public LaunchManifest(
            string? displayName,
            string? sourceSha256,
            string? targetSha256,
            IReadOnlyList<string> patches)
        {
            DisplayName = displayName;
            SourceSha256 = sourceSha256;
            TargetSha256 = targetSha256;
            Patches = patches;
        }

        public string? DisplayName { get; }
        public string? SourceSha256 { get; }
        public string? TargetSha256 { get; }
        public IReadOnlyList<string> Patches { get; }
    }
}
