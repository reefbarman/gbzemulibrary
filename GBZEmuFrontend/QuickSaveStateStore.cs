using GBZEmuLibrary;

namespace GBZEmuFrontend;

/// <summary>
/// Persists one atomic quick-save state per resolved ROM identity beneath the frontend save directory.
/// </summary>
internal sealed class QuickSaveStateStore
{
    public string StatePath { get; }

    public QuickSaveStateStore(string saveDirectory, string persistenceIdentity)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory))
        {
            throw new ArgumentException("A save directory is required.", nameof(saveDirectory));
        }

        if (string.IsNullOrWhiteSpace(persistenceIdentity))
        {
            throw new ArgumentException("A ROM persistence identity is required.", nameof(persistenceIdentity));
        }

        if (persistenceIdentity is "." or ".." ||
            Path.IsPathRooted(persistenceIdentity) ||
            persistenceIdentity.Contains('/') ||
            persistenceIdentity.Contains('\\') ||
            persistenceIdentity.IndexOfAny("<>:\"|?*".ToCharArray()) >= 0 ||
            !string.Equals(Path.GetFileName(persistenceIdentity), persistenceIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException("The ROM persistence identity must be a portable leaf filename.", nameof(persistenceIdentity));
        }

        StatePath = Path.Combine(saveDirectory, "States", $"{persistenceIdentity}.state");
    }

    /// <summary>
    /// Writes a state through a sibling temporary file so an interrupted save cannot replace the previous state.
    /// </summary>
    public void Save(Emulator emulator)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{StatePath}.tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, emulator.CaptureState().ToArray());
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Restores the quick-save state when one exists.
    /// </summary>
    public bool TryLoad(Emulator emulator)
    {
        if (!File.Exists(StatePath))
        {
            return false;
        }

        emulator.RestoreState(EmulatorState.FromArray(File.ReadAllBytes(StatePath)));
        return true;
    }
}
