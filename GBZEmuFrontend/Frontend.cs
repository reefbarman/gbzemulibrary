using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GBZEmuLibrary;
using Raylib_cs;
using EmulatorSound = GBZEmuLibrary.Sound;
using RaylibColor = Raylib_cs.Color;

namespace GBZEmuFrontend;

internal sealed class Frontend : IDisposable
{
    private const int PresentationFramesPerSecond = 60;
    private const int AudioFramesPerBuffer = EmulatorSound.SAMPLE_RATE / PresentationFramesPerSecond;
    private const int AudioQueueCapacityFrames = AudioFramesPerBuffer * 6;
    private const int AudioStartupFrames = AudioFramesPerBuffer * 2;
    private const int MaxCatchUpFrames = 5;
    private const int FastForwardMultiplier = 4;
    private const int MaxGamepads = 4;
    private const float GamepadAxisThreshold = 0.5f;
    private const float RumbleLowMotorStrength = 0.75f;
    private const float RumbleHighMotorStrength = 0.25f;
    private const float RumbleRefreshDuration = 0.25f;
    private const double FrameStepRepeatDelay = 0.4;
    private const double FrameStepRepeatInterval = 1.0 / 15.0;
    private const double RewindRepeatInterval = 1.0 / 15.0;

    private readonly Emulator _emulator = new();
    private readonly FrameBlender _frameBlender = new();
    private readonly FrontendRewindController _rewind = new();
    private readonly RaylibColor[] _pixels = new RaylibColor[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
    private readonly RaylibColor[] _sgbPixels = new RaylibColor[
        SuperGameBoyDisplay.HORIZONTAL_RESOLUTION * SuperGameBoyDisplay.VERTICAL_RESOLUTION];
    private readonly FrontendAudioQueue _audioQueue = new(AudioQueueCapacityFrames, AudioStartupFrames);
    private readonly bool[] _logicalButtonStates = new bool[(int)JoypadButtons.Count];
    private readonly bool[] _desiredButtonStates = new bool[(int)JoypadButtons.Count];
    private readonly bool[,] _sgbLogicalButtonStates = new bool[4, (int)JoypadButtons.Count];
    private readonly bool[,] _sgbDesiredButtonStates = new bool[4, (int)JoypadButtons.Count];
    private readonly Dictionary<KeyboardKey, JoypadButtons> _keyMap = new()
    {
        [KeyboardKey.Right] = JoypadButtons.Right,
        [KeyboardKey.Left] = JoypadButtons.Left,
        [KeyboardKey.Up] = JoypadButtons.Up,
        [KeyboardKey.Down] = JoypadButtons.Down,
        [KeyboardKey.X] = JoypadButtons.A,
        [KeyboardKey.Z] = JoypadButtons.B,
        [KeyboardKey.RightShift] = JoypadButtons.Select,
        [KeyboardKey.Enter] = JoypadButtons.Start
    };
    private readonly Dictionary<GamepadButton, JoypadButtons> _gamepadMap = new()
    {
        [GamepadButton.LeftFaceRight] = JoypadButtons.Right,
        [GamepadButton.LeftFaceLeft] = JoypadButtons.Left,
        [GamepadButton.LeftFaceUp] = JoypadButtons.Up,
        [GamepadButton.LeftFaceDown] = JoypadButtons.Down,
        [GamepadButton.RightFaceRight] = JoypadButtons.A,
        [GamepadButton.RightFaceDown] = JoypadButtons.B,
        [GamepadButton.MiddleLeft] = JoypadButtons.Select,
        [GamepadButton.MiddleRight] = JoypadButtons.Start
    };

    // Raylib exposes no callback context pointer; the test frontend supports one live audio stream at a time.
    private static volatile FrontendAudioQueue? s_audioQueue;

    private Texture2D _texture;
    private AudioStream _audioStream;
    private string _windowTitle = string.Empty;
    private string _statusText = string.Empty;
    private bool _started;
    private bool _windowReady;
    private bool _textureReady;
    private bool _audioReady;
    private bool _audioPlaybackStarted;
    private bool _paused;
    private bool _waitingForInputRelease;
    private bool _frameStepRepeatArmed;
    private bool _audioSuspended;
    private string _persistenceId = VideoFilterPresetCatalog.ClassicPersistenceId;
    private bool _correctCgbColors;
    private bool _videoFrameReady;
    private bool _fastForwarding;
    private bool _rewinding;
    private float _pendingRumbleStrength;
    private bool _rumbleDirty;
    private int _activeGamepad = -1;
    private int _rumbleGamepad = -1;
    private int _videoWidth = Display.HORIZONTAL_RESOLUTION;
    private int _videoHeight = Display.VERTICAL_RESOLUTION;
    private double _nextFrameStepTime;
    private double _nextRewindTime;
    private double _lastUpdateTime;
    private double _frameDuration = 1.0 / Display.FRAME_RATE;
    private double _frameAccumulator;
    private double _statusUntil;
    private QuickSaveStateStore? _quickState;
    private FrontendSpatialFilterOverlays? _spatialFilterOverlays;

    public void Run(
        FrontendOptions options,
        FrontendSettingsStore settingsStore,
        FrontendSettingsLoadResult loadedSettings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(loadedSettings);

        var resolvedVideo = options.ResolveVideoSettings(loadedSettings.Settings);
        var resolvedRom = options.ROMPath == null
            ? null
            : RomLaunchResolver.Resolve(options.ROMPath);
        var pickerScale = Math.Max(4, resolvedVideo.Effective.IntegerScale);
        var initialScale = resolvedRom == null ? pickerScale : resolvedVideo.Effective.IntegerScale;

        Raylib.SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.HighDpiWindow);
        Raylib.InitWindow(
            Display.HORIZONTAL_RESOLUTION * initialScale,
            Display.VERTICAL_RESOLUTION * initialScale,
            "GBZEmuFrontend - Select ROM");
        Raylib.SetExitKey(KeyboardKey.Null);
        _windowReady = true;
        Raylib.SetTargetFPS(PresentationFramesPerSecond);

        if (resolvedRom == null)
        {
            (resolvedRom, resolvedVideo) = SelectROM(
                options.ROMDirectory!,
                options,
                settingsStore,
                resolvedVideo);
        }

        if (resolvedRom == null)
        {
            return;
        }

        ReportResolvedRom(resolvedRom);
        var videoSettings = resolvedVideo.Effective;
        var scale = videoSettings.IntegerScale;
        _waitingForInputRelease = options.ROMPath == null;
        _persistenceId = videoSettings.Persistence;
        _frameBlender.Reset();

        var compatibility = resolvedRom.CartridgeInspection.Compatibility;
        var hardwareModel = options.HardwareModel ?? ResolveAutomaticHardwareModel(compatibility);
        if (HardwareModelMetadata.IsImplemented(hardwareModel) &&
            !HardwareModelMetadata.SupportsCartridge(hardwareModel, compatibility))
        {
            throw new ArgumentException(
                $"Hardware model {hardwareModel} does not support {compatibility} cartridges.");
        }

        var cgbAudioFilter = ShouldUseCgbAudioFilter(hardwareModel);
        _correctCgbColors = ShouldCorrectCgbColors(
            hardwareModel,
            compatibility,
            videoSettings.CgbColorProfile == VideoFilterPresetCatalog.RawColorProfileId);
        var bootRom = options.SkipBootROM
            ? BootRomConfig.Skip()
            : options.BootROMPath == null
                ? BootRomConfig.BuiltIn()
                : BootRomConfig.ExternalFile(options.BootROMPath);

        _started = _emulator.Start(new Emulator.Config(hardwareModel)
        {
            ROMBytes = resolvedRom.EffectiveBytes,
            ROMIdentity = resolvedRom.PersistenceIdentity,
            SaveLocation = options.SaveDirectory,
            BootRom = bootRom
        });

        if (!_started)
        {
            throw new InvalidOperationException($"Failed to load ROM: {resolvedRom.BaseRomPath}");
        }

        _frameDuration = 1.0 / _emulator.FrameRate;

        _quickState = new QuickSaveStateStore(options.SaveDirectory, resolvedRom.PersistenceIdentity);
        _rewind.Reset(_emulator);
        _pendingRumbleStrength = _emulator.RumbleStrength;
        _rumbleDirty = true;
        _emulator.RumbleStrengthUpdated += HandleRumbleStrengthUpdated;

        _windowTitle = $"GBZEmuFrontend - {resolvedRom.DisplayName}";
        Raylib.SetWindowTitle(_windowTitle);

        if (_emulator.IsSuperGameBoy)
        {
            _videoWidth = SuperGameBoyDisplay.HORIZONTAL_RESOLUTION;
            _videoHeight = SuperGameBoyDisplay.VERTICAL_RESOLUTION;
        }

        Raylib.SetWindowSize(_videoWidth * scale, _videoHeight * scale);

        var image = Raylib.GenImageColor(_videoWidth, _videoHeight, RaylibColor.Black);
        _texture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        Raylib.SetTextureFilter(_texture, TextureFilter.Point);
        _textureReady = true;

        if (!_emulator.IsSuperGameBoy)
        {
            _spatialFilterOverlays = new FrontendSpatialFilterOverlays(
                _videoWidth,
                _videoHeight,
                scale,
                videoSettings.PixelGrid,
                videoSettings.Glare);
        }

        Raylib.InitAudioDevice();
        _audioReady = Raylib.IsAudioDeviceReady();
        if (_audioReady)
        {
            _audioQueue.SetHardwareModel(cgbAudioFilter);
            Raylib.SetAudioStreamBufferSizeDefault(AudioFramesPerBuffer);
            _audioStream = Raylib.LoadAudioStream(EmulatorSound.SAMPLE_RATE, 32, 2);
            s_audioQueue = _audioQueue;
            unsafe
            {
                Raylib.SetAudioStreamCallback(_audioStream, &FillAudioStream);
            }
        }
        else
        {
            Console.Error.WriteLine("Audio device initialization failed; continuing without sound.");
        }

        if (options.StartPaused)
        {
            SetPaused(true);
        }

        _lastUpdateTime = Raylib.GetTime();

        while (!Raylib.WindowShouldClose())
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                break;
            }

            UpdateInput();

            var stateRestored = HandleQuickStateControls();
            var rewindHeld = HandleRewind(out var rewindRestored);
            var stepFrame = ShouldStepFrame();
            SetFastForwarding(!stateRestored && !rewindHeld && !_paused && IsFastForwardRequested());

            var frameChanged = stateRestored || rewindRestored;
            if (!stateRestored && !rewindHeld && AdvanceEmulation(stepFrame))
            {
                frameChanged = true;
            }

            if (frameChanged)
            {
                UpdateVideo();
            }

            UpdateAudio();
            UpdateRumble();
            UpdateWindowTitle();
            Draw(scale);
        }
    }

    public void Dispose()
    {
        StopRumble();

        if (_audioReady)
        {
            Raylib.StopAudioStream(_audioStream);
            s_audioQueue = null;
            Raylib.UnloadAudioStream(_audioStream);
            Raylib.CloseAudioDevice();
            _audioReady = false;
        }

        if (_windowReady)
        {
            _spatialFilterOverlays?.Dispose();
            _spatialFilterOverlays = null;

            if (_textureReady)
            {
                Raylib.UnloadTexture(_texture);
                _textureReady = false;
            }

            Raylib.CloseWindow();
            _windowReady = false;
        }

        if (_started)
        {
            ReleaseLogicalButtons();
            _emulator.RumbleStrengthUpdated -= HandleRumbleStrengthUpdated;
            _emulator.Terminate();
            _started = false;
        }
    }


    /// <summary>
    /// Resolves the default concrete hardware model from cartridge compatibility.
    /// </summary>
    internal static HardwareModel ResolveAutomaticHardwareModel(CartridgeCompatibility compatibility)
    {
        return compatibility == CartridgeCompatibility.DmgOnly
            ? HardwareModel.DmgB
            : HardwareModel.CgbE;
    }

    /// <summary>
    /// Selects the measured CGB filter or the retained DMG approximation for the concrete model.
    /// </summary>
    internal static bool ShouldUseCgbAudioFilter(HardwareModel hardwareModel)
    {
        return hardwareModel switch
        {
            HardwareModel.DmgB => false,
            HardwareModel.Mgb => false,
            HardwareModel.CgbE => true,
            HardwareModel.Sgb2 => false,
            // Retain the CGB-like DC-block approximation until a measured AGB coefficient is available.
            HardwareModel.AgbA => true,
            _ => throw new NotSupportedException(
                $"Hardware model {hardwareModel} does not have an implemented frontend audio profile.")
        };
    }

    /// <summary>
    /// Determines whether native CGB execution should use the frontend color profile.
    /// </summary>
    internal static bool ShouldCorrectCgbColors(
        HardwareModel hardwareModel,
        CartridgeCompatibility compatibility,
        bool rawColors)
    {
        return hardwareModel == HardwareModel.CgbE &&
            compatibility != CartridgeCompatibility.DmgOnly &&
            !rawColors;
    }

    private static void ReportResolvedRom(ResolvedRomImage resolvedRom)
    {
        Console.WriteLine($"ROM source: {Path.GetFileName(resolvedRom.BaseRomPath)}");
        Console.WriteLine($"Base SHA-256: {resolvedRom.BaseSha256}");
        Console.WriteLine($"Effective SHA-256: {resolvedRom.EffectiveSha256}");
        Console.WriteLine($"Persistence identity: {resolvedRom.PersistenceIdentity}");
        for (var index = 0; index < resolvedRom.AppliedPatches.Count; index++)
        {
            var patch = resolvedRom.AppliedPatches[index];
            Console.WriteLine(
                $"Patch {index + 1}: {patch.FileName} ({patch.Format.ToString().ToUpperInvariant()}, SHA-256 {patch.Sha256})");
        }

        foreach (var diagnostic in resolvedRom.CartridgeInspection.Diagnostics)
        {
            Console.Error.WriteLine($"ROM warning: {diagnostic.Message}");
        }
    }

    private static (ResolvedRomImage? Rom, ResolvedVideoFilterSettings VideoSettings) SelectROM(
        string romDirectory,
        FrontendOptions options,
        FrontendSettingsStore settingsStore,
        ResolvedVideoFilterSettings resolvedVideo)
    {
        var romPaths = Directory.GetFiles(romDirectory)
            .Where(path => path.EndsWith(".gb", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedIndex = 0;
        FrontendSettingsMenu? settingsMenu = null;
        string? settingsDiagnostic = null;
        string? launchDiagnostic = null;

        while (!Raylib.WindowShouldClose())
        {
            var gamepad = FindAvailableGamepad();
            if (settingsMenu == null)
            {
                if (Raylib.IsKeyPressed(KeyboardKey.Escape)
                    || IsGamepadButtonPressed(gamepad, GamepadButton.RightFaceRight))
                {
                    return (null, resolvedVideo);
                }

                if (Raylib.IsKeyPressed(KeyboardKey.S)
                    || IsGamepadButtonPressed(gamepad, GamepadButton.RightFaceUp))
                {
                    settingsMenu = new FrontendSettingsMenu(options, resolvedVideo);
                    settingsDiagnostic = null;
                    continue;
                }

                if (romPaths.Length > 0)
                {
                    if (Raylib.IsKeyPressed(KeyboardKey.Up)
                        || IsGamepadButtonPressed(gamepad, GamepadButton.LeftFaceUp))
                    {
                        selectedIndex = (selectedIndex + romPaths.Length - 1) % romPaths.Length;
                        launchDiagnostic = null;
                    }

                    if (Raylib.IsKeyPressed(KeyboardKey.Down)
                        || IsGamepadButtonPressed(gamepad, GamepadButton.LeftFaceDown))
                    {
                        selectedIndex = (selectedIndex + 1) % romPaths.Length;
                        launchDiagnostic = null;
                    }

                    if (Raylib.IsKeyPressed(KeyboardKey.Enter)
                        || IsGamepadButtonPressed(gamepad, GamepadButton.RightFaceDown))
                    {
                        try
                        {
                            return (RomLaunchResolver.Resolve(romPaths[selectedIndex]), resolvedVideo);
                        }
                        catch (Exception exception) when (exception is IOException
                            or UnauthorizedAccessException
                            or NotSupportedException
                            or ArgumentException)
                        {
                            launchDiagnostic = exception.Message;
                        }
                    }
                }

                DrawROMPicker(romDirectory, romPaths, selectedIndex, launchDiagnostic);
                continue;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Escape)
                || IsGamepadButtonPressed(gamepad, GamepadButton.RightFaceRight))
            {
                settingsMenu = null;
                settingsDiagnostic = null;
                continue;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Up)
                || IsGamepadButtonPressed(gamepad, GamepadButton.LeftFaceUp))
            {
                settingsMenu.MoveSelection(-1);
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Down)
                || IsGamepadButtonPressed(gamepad, GamepadButton.LeftFaceDown))
            {
                settingsMenu.MoveSelection(1);
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Left)
                || IsGamepadButtonPressed(gamepad, GamepadButton.LeftFaceLeft))
            {
                settingsMenu.AdjustSelected(-1);
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Right)
                || IsGamepadButtonPressed(gamepad, GamepadButton.LeftFaceRight))
            {
                settingsMenu.AdjustSelected(1);
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Enter)
                || IsGamepadButtonPressed(gamepad, GamepadButton.RightFaceDown))
            {
                var action = settingsMenu.ActivateSelected();
                if (action == FrontendSettingsMenuAction.Back)
                {
                    settingsMenu = null;
                    settingsDiagnostic = null;
                    continue;
                }

                if (action == FrontendSettingsMenuAction.Apply)
                {
                    try
                    {
                        settingsStore.Save(settingsMenu.WorkingSettings);
                        resolvedVideo = options.ResolveVideoSettings(settingsMenu.WorkingSettings);
                        settingsMenu = null;
                        settingsDiagnostic = null;
                        var pickerScale = Math.Max(4, resolvedVideo.Effective.IntegerScale);
                        Raylib.SetWindowSize(
                            Display.HORIZONTAL_RESOLUTION * pickerScale,
                            Display.VERTICAL_RESOLUTION * pickerScale);
                        continue;
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException)
                    {
                        settingsDiagnostic = $"Could not save settings: {exception.Message}";
                    }
                }
            }

            DrawSettingsMenu(settingsMenu!, settingsDiagnostic);
        }

        return (null, resolvedVideo);
    }

    private static void DrawROMPicker(
        string romDirectory,
        string[] romPaths,
        int selectedIndex,
        string? diagnostic)
    {
        const int padding = 24;
        const int titleFontSize = 24;
        const int itemFontSize = 18;
        const int itemHeight = 26;
        const int listTop = 84;

        var visibleItems = Math.Max(1, (Raylib.GetScreenHeight() - listTop - 44) / itemHeight);
        var firstVisible = Math.Max(0, selectedIndex - visibleItems + 1);
        var lastVisible = Math.Min(romPaths.Length, firstVisible + visibleItems);

        Raylib.BeginDrawing();
        Raylib.ClearBackground(RaylibColor.Black);
        Raylib.DrawText("Select a ROM", padding, 20, titleFontSize, RaylibColor.RayWhite);
        Raylib.DrawText(romDirectory, padding, 52, 14, RaylibColor.Gray);
        Raylib.DrawText(
            "Up/Down: Select   Enter/A: Play   S/Y: Settings   Escape/B: Quit",
            padding,
            Raylib.GetScreenHeight() - 20,
            12,
            RaylibColor.Gray);

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            Raylib.DrawText(diagnostic, padding, listTop, 14, RaylibColor.Red);
        }
        else if (romPaths.Length == 0)
        {
            Raylib.DrawText("No .gb or .gbc files found.", padding, listTop, itemFontSize, RaylibColor.Gray);
        }
        else
        {
            for (var i = firstVisible; i < lastVisible; i++)
            {
                var color = i == selectedIndex ? RaylibColor.Yellow : RaylibColor.RayWhite;
                var prefix = i == selectedIndex ? "> " : "  ";
                Raylib.DrawText(
                    $"{prefix}{Path.GetFileName(romPaths[i])}",
                    padding,
                    listTop + ((i - firstVisible) * itemHeight),
                    itemFontSize,
                    color);
            }
        }

        Raylib.EndDrawing();
    }

    private static void DrawSettingsMenu(FrontendSettingsMenu menu, string? diagnostic)
    {
        const int padding = 24;
        const int titleFontSize = 24;
        const int rowFontSize = 18;
        const int rowHeight = 34;
        const int rowsTop = 76;
        const int valueColumn = 248;

        Raylib.BeginDrawing();
        Raylib.ClearBackground(RaylibColor.Black);
        Raylib.DrawText("Video Settings", padding, 20, titleFontSize, RaylibColor.RayWhite);
        Raylib.DrawText("Applied before launch; CLI-owned rows are read-only.", padding, 50, 14, RaylibColor.Gray);

        for (var index = 0; index < (int)FrontendSettingsMenuRow.Count; index++)
        {
            var row = (FrontendSettingsMenuRow)index;
            var selected = row == menu.SelectedRow;
            var overridden = menu.IsRowOverridden(row);
            var color = selected ? RaylibColor.Yellow : overridden ? RaylibColor.Gray : RaylibColor.RayWhite;
            var prefix = selected ? "> " : "  ";
            var label = GetSettingsRowLabel(row);
            var y = rowsTop + (index * rowHeight);
            Raylib.DrawText($"{prefix}{label}", padding, y, rowFontSize, color);

            var value = menu.GetVisibleValue(row);
            if (!string.IsNullOrEmpty(value))
            {
                var suffix = overridden ? "  [CLI override]" : string.Empty;
                Raylib.DrawText($"{value}{suffix}", valueColumn, y, rowFontSize, color);
            }
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            Raylib.DrawText(diagnostic, padding, Raylib.GetScreenHeight() - 44, 14, RaylibColor.Red);
        }

        Raylib.DrawText(
            "Up/Down: Row   Left/Right: Value   Enter/A: Activate   Escape/B: Cancel",
            padding,
            Raylib.GetScreenHeight() - 20,
            12,
            RaylibColor.Gray);
        Raylib.EndDrawing();
    }

    private static string GetSettingsRowLabel(FrontendSettingsMenuRow row)
    {
        return row switch
        {
            FrontendSettingsMenuRow.Preset => "Preset",
            FrontendSettingsMenuRow.CgbColor => "CGB color",
            FrontendSettingsMenuRow.Persistence => "Persistence / ghosting",
            FrontendSettingsMenuRow.PixelGrid => "Pixel grid",
            FrontendSettingsMenuRow.Glare => "Glare",
            FrontendSettingsMenuRow.IntegerScale => "Integer scale",
            FrontendSettingsMenuRow.Apply => "Apply",
            FrontendSettingsMenuRow.ResetDefaults => "Reset defaults",
            FrontendSettingsMenuRow.Back => "Back",
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, null)
        };
    }

    private void UpdateInput()
    {
        _activeGamepad = FindAvailableGamepad();

        if (_waitingForInputRelease)
        {
            _waitingForInputRelease = IsAnyGameplayInputDown();
            return;
        }

        Array.Clear(_desiredButtonStates, 0, _desiredButtonStates.Length);
        foreach (var binding in _keyMap)
        {
            if (Raylib.IsKeyDown(binding.Key))
            {
                _desiredButtonStates[(int)binding.Value] = true;
            }
        }

        if (_activeGamepad >= 0)
        {
            foreach (var binding in _gamepadMap)
            {
                if (Raylib.IsGamepadButtonDown(_activeGamepad, binding.Key))
                {
                    _desiredButtonStates[(int)binding.Value] = true;
                }
            }

            var horizontal = Raylib.GetGamepadAxisMovement(_activeGamepad, GamepadAxis.LeftX);
            var vertical = Raylib.GetGamepadAxisMovement(_activeGamepad, GamepadAxis.LeftY);
            _desiredButtonStates[(int)JoypadButtons.Left] |= horizontal < -GamepadAxisThreshold;
            _desiredButtonStates[(int)JoypadButtons.Right] |= horizontal > GamepadAxisThreshold;
            _desiredButtonStates[(int)JoypadButtons.Up] |= vertical < -GamepadAxisThreshold;
            _desiredButtonStates[(int)JoypadButtons.Down] |= vertical > GamepadAxisThreshold;
        }

        for (var i = 0; i < _logicalButtonStates.Length; i++)
        {
            if (_logicalButtonStates[i] == _desiredButtonStates[i])
            {
                continue;
            }

            _logicalButtonStates[i] = _desiredButtonStates[i];
            if (_logicalButtonStates[i])
            {
                _emulator.ButtonDown((JoypadButtons)i);
            }
            else
            {
                _emulator.ButtonUp((JoypadButtons)i);
            }
        }

        UpdateSgbMultiplayerInput();
    }

    private void UpdateSgbMultiplayerInput()
    {
        if (!_emulator.IsSuperGameBoy)
        {
            return;
        }

        for (var player = 1; player < 4; player++)
        {
            for (var button = 0; button < (int)JoypadButtons.Count; button++)
            {
                _sgbDesiredButtonStates[player, button] = false;
            }

            var gamepad = player < _emulator.SuperGameBoyPlayerCount ? FindAvailableGamepad(player) : -1;
            if (gamepad >= 0)
            {
                foreach (var binding in _gamepadMap)
                {
                    _sgbDesiredButtonStates[player, (int)binding.Value] |= Raylib.IsGamepadButtonDown(gamepad, binding.Key);
                }

                var horizontal = Raylib.GetGamepadAxisMovement(gamepad, GamepadAxis.LeftX);
                var vertical = Raylib.GetGamepadAxisMovement(gamepad, GamepadAxis.LeftY);
                _sgbDesiredButtonStates[player, (int)JoypadButtons.Left] |= horizontal < -GamepadAxisThreshold;
                _sgbDesiredButtonStates[player, (int)JoypadButtons.Right] |= horizontal > GamepadAxisThreshold;
                _sgbDesiredButtonStates[player, (int)JoypadButtons.Up] |= vertical < -GamepadAxisThreshold;
                _sgbDesiredButtonStates[player, (int)JoypadButtons.Down] |= vertical > GamepadAxisThreshold;
            }

            for (var button = 0; button < (int)JoypadButtons.Count; button++)
            {
                if (_sgbLogicalButtonStates[player, button] == _sgbDesiredButtonStates[player, button])
                {
                    continue;
                }

                _sgbLogicalButtonStates[player, button] = _sgbDesiredButtonStates[player, button];
                if (_sgbLogicalButtonStates[player, button])
                {
                    _emulator.ButtonDown((JoypadButtons)button, player);
                }
                else
                {
                    _emulator.ButtonUp((JoypadButtons)button, player);
                }
            }
        }
    }

    private bool IsAnyGameplayInputDown()
    {
        foreach (var binding in _keyMap)
        {
            if (Raylib.IsKeyDown(binding.Key))
            {
                return true;
            }
        }

        if (_activeGamepad < 0)
        {
            return false;
        }

        foreach (var binding in _gamepadMap)
        {
            if (Raylib.IsGamepadButtonDown(_activeGamepad, binding.Key))
            {
                return true;
            }
        }

        return Math.Abs(Raylib.GetGamepadAxisMovement(_activeGamepad, GamepadAxis.LeftX)) > GamepadAxisThreshold ||
               Math.Abs(Raylib.GetGamepadAxisMovement(_activeGamepad, GamepadAxis.LeftY)) > GamepadAxisThreshold;
    }

    private void ReleaseLogicalButtons()
    {
        for (var i = 0; i < _logicalButtonStates.Length; i++)
        {
            if (!_logicalButtonStates[i])
            {
                continue;
            }

            _logicalButtonStates[i] = false;
            _emulator.ButtonUp((JoypadButtons)i);
        }

        for (var player = 1; player < 4; player++)
        {
            for (var button = 0; button < (int)JoypadButtons.Count; button++)
            {
                if (!_sgbLogicalButtonStates[player, button])
                {
                    continue;
                }

                _sgbLogicalButtonStates[player, button] = false;
                _emulator.ButtonUp((JoypadButtons)button, player);
            }
        }
    }

    private bool HandleQuickStateControls()
    {
        if (_quickState == null)
        {
            return false;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.F5) ||
            IsGamepadButtonPressed(_activeGamepad, GamepadButton.RightFaceUp))
        {
            try
            {
                _quickState.Save(_emulator);
                ShowStatus("STATE SAVED");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Failed to save state: {exception.Message}");
                ShowStatus("SAVE FAILED");
            }

            return false;
        }

        if (!Raylib.IsKeyPressed(KeyboardKey.F8) &&
            !IsGamepadButtonPressed(_activeGamepad, GamepadButton.RightFaceLeft))
        {
            return false;
        }

        try
        {
            if (!_quickState.TryLoad(_emulator))
            {
                ShowStatus("NO SAVE STATE");
                return false;
            }

            ReapplyLogicalButtons();
            _rewind.Reset(_emulator);
            _frameAccumulator = 0;
            PresentRestoredFrame();
            ShowStatus("STATE LOADED");
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Failed to load state: {exception.Message}");
            ShowStatus("LOAD FAILED");
            return false;
        }
    }

    private bool HandleRewind(out bool frameRestored)
    {
        frameRestored = false;
        var requested = Raylib.IsKeyDown(KeyboardKey.R) ||
                        IsGamepadButtonDown(_activeGamepad, GamepadButton.LeftTrigger1);

        if (!requested)
        {
            if (_rewinding)
            {
                _rewinding = false;
                UpdateAudioSuspension();
                RefreshWindowTitle();
            }

            return false;
        }

        var now = Raylib.GetTime();
        if (!_rewinding)
        {
            _rewinding = true;
            _nextRewindTime = now;
            SetFastForwarding(false);
            ResetAudioQueue();
            UpdateAudioSuspension();
            RefreshWindowTitle();
        }

        _frameAccumulator = 0;
        if (now < _nextRewindTime)
        {
            return true;
        }

        _nextRewindTime = now + RewindRepeatInterval;
        if (!_rewind.TryRewind(_emulator))
        {
            ShowStatus("REWIND LIMIT");
            return true;
        }

        ReapplyLogicalButtons();
        PresentRestoredFrame();
        frameRestored = true;
        return true;
    }

    private bool IsFastForwardRequested()
    {
        return Raylib.IsKeyDown(KeyboardKey.Tab) ||
               IsGamepadButtonDown(_activeGamepad, GamepadButton.RightTrigger1);
    }

    private void SetFastForwarding(bool fastForwarding)
    {
        if (_fastForwarding == fastForwarding)
        {
            return;
        }

        _fastForwarding = fastForwarding;
        ResetAudioQueue();
        UpdateAudioSuspension();
        RefreshWindowTitle();
    }

    private void PresentRestoredFrame()
    {
        _emulator.GetSoundSamples(out _);
        _frameBlender.Reset();
        ProcessVideoFrame();
        _videoFrameReady = true;
        ResetAudioQueue(restartDevice: true);
    }

    /// <summary>
    /// Replaces the historical joypad latch restored from a state with the host's current merged input state.
    /// </summary>
    private void ReapplyLogicalButtons()
    {
        for (var i = 0; i < _logicalButtonStates.Length; i++)
        {
            _emulator.ButtonUp((JoypadButtons)i);
            if (_logicalButtonStates[i])
            {
                _emulator.ButtonDown((JoypadButtons)i);
            }
        }

        for (var player = 1; player < 4; player++)
        {
            for (var button = 0; button < (int)JoypadButtons.Count; button++)
            {
                _emulator.ButtonUp((JoypadButtons)button, player);
                if (_sgbLogicalButtonStates[player, button])
                {
                    _emulator.ButtonDown((JoypadButtons)button, player);
                }
            }
        }
    }

    private bool ShouldStepFrame()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.P) ||
            IsGamepadButtonPressed(_activeGamepad, GamepadButton.LeftThumb))
        {
            SetPaused(!_paused);
        }

        if (!_paused)
        {
            ResetFrameStepRepeat();
            return false;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.N) ||
            IsGamepadButtonPressed(_activeGamepad, GamepadButton.RightThumb))
        {
            _frameStepRepeatArmed = true;
            _nextFrameStepTime = Raylib.GetTime() + FrameStepRepeatDelay;
            return true;
        }

        if (!Raylib.IsKeyDown(KeyboardKey.N) &&
            !IsGamepadButtonDown(_activeGamepad, GamepadButton.RightThumb))
        {
            ResetFrameStepRepeat();
            return false;
        }

        if (!_frameStepRepeatArmed || Raylib.GetTime() < _nextFrameStepTime)
        {
            return false;
        }

        _nextFrameStepTime = Raylib.GetTime() + FrameStepRepeatInterval;
        return true;
    }

    private bool AdvanceEmulation(bool stepFrame)
    {
        var now = Raylib.GetTime();
        var elapsed = now - _lastUpdateTime;
        _lastUpdateTime = now;

        if (_paused)
        {
            _frameAccumulator = 0;

            if (!stepFrame)
            {
                return false;
            }

            AdvanceFrame(false);
            ResetAudioQueue();
            return true;
        }

        _frameAccumulator += Math.Min(elapsed, _frameDuration * MaxCatchUpFrames);

        var framesAdvanced = 0;
        while (_frameAccumulator >= _frameDuration && framesAdvanced < MaxCatchUpFrames)
        {
            if (_fastForwarding)
            {
                var completed = _emulator.FastForward(FastForwardMultiplier);
                if (completed == 0)
                {
                    break;
                }

                _rewind.FramesAdvanced(_emulator, completed);
                ProcessVideoFrame();
                _videoFrameReady = true;
            }
            else
            {
                AdvanceFrame(true);
            }

            _frameAccumulator -= _frameDuration;
            framesAdvanced++;
        }

        return framesAdvanced > 0;
    }

    private void AdvanceFrame(bool queueAudio)
    {
        _emulator.Update();
        _rewind.FramesAdvanced(_emulator, 1);
        ProcessVideoFrame();
        _videoFrameReady = true;
        var source = _emulator.GetSoundSamples(out var sampleFrameCount);

        if (queueAudio && _audioReady)
        {
            QueueAudio(source, sampleFrameCount);
        }
    }

    private void ResetFrameStepRepeat()
    {
        _frameStepRepeatArmed = false;
        _nextFrameStepTime = 0;
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        ResetFrameStepRepeat();
        ResetAudioQueue();
        if (paused)
        {
            StopRumble();
        }

        UpdateAudioSuspension();
        RefreshWindowTitle();
    }

    private void UpdateVideo()
    {
        if (!_videoFrameReady)
        {
            return;
        }

        Raylib.UpdateTexture(_texture, _emulator.IsSuperGameBoy ? _sgbPixels : _pixels);
        _videoFrameReady = false;
    }

    private void ProcessVideoFrame()
    {
        if (!_emulator.IsSuperGameBoy)
        {
            _frameBlender.Process(_emulator.GetScreenData(), _pixels, _persistenceId, _correctCgbColors);
            return;
        }

        var source = _emulator.GetSuperGameBoyScreenData();
        var destination = 0;
        for (var y = 0; y < SuperGameBoyDisplay.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < SuperGameBoyDisplay.HORIZONTAL_RESOLUTION; x++)
            {
                var color = source[x, y];
                _sgbPixels[destination++] = new RaylibColor(color.R, color.G, color.B, byte.MaxValue);
            }
        }
    }

    private void QueueAudio(float[] source, int frameCount)
    {
        _audioQueue.Enqueue(source, frameCount);
    }

    private void UpdateAudio()
    {
        if (_audioSuspended || !_audioReady)
        {
            return;
        }

        if (_audioPlaybackStarted)
        {
            if (!_audioQueue.IsPrimed)
            {
                Raylib.StopAudioStream(_audioStream);
                _audioPlaybackStarted = false;
            }

            return;
        }

        if (_audioQueue.IsPrimed)
        {
            Raylib.PlayAudioStream(_audioStream);
            _audioPlaybackStarted = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void FillAudioStream(void* bufferData, uint frameCount)
    {
        var destination = new Span<float>(bufferData, (int)frameCount * 2);
        var queue = s_audioQueue;
        if (queue == null || !queue.TryDequeue(destination, (int)frameCount))
        {
            destination.Clear();
        }
    }

    private void ResetAudioQueue(bool restartDevice = false)
    {
        _audioQueue.Reset();

        if (!_audioReady || (!_audioPlaybackStarted && !restartDevice))
        {
            return;
        }

        Raylib.StopAudioStream(_audioStream);
        _audioPlaybackStarted = false;
    }

    private void UpdateAudioSuspension()
    {
        if (!_audioReady)
        {
            return;
        }

        var shouldSuspend = _paused || _fastForwarding || _rewinding;
        if (_audioSuspended == shouldSuspend)
        {
            return;
        }

        _audioSuspended = shouldSuspend;
        if (shouldSuspend)
        {
            if (_audioPlaybackStarted)
            {
                Raylib.PauseAudioStream(_audioStream);
            }
            return;
        }

        if (_audioPlaybackStarted)
        {
            Raylib.StopAudioStream(_audioStream);
            _audioPlaybackStarted = false;
        }
    }

    private void HandleRumbleStrengthUpdated(float strength)
    {
        _pendingRumbleStrength = Math.Max(_pendingRumbleStrength, Math.Clamp(strength, 0f, 1f));
        _rumbleDirty = true;
    }

    private void UpdateRumble()
    {
        if (_rumbleGamepad != _activeGamepad)
        {
            StopRumble();
            _pendingRumbleStrength = 0f;
            _rumbleGamepad = _activeGamepad;
            _rumbleDirty = true;
        }

        if (_rumbleGamepad < 0)
        {
            return;
        }

        if (!_rumbleDirty)
        {
            return;
        }

        var strength = _pendingRumbleStrength;
        _pendingRumbleStrength = 0f;
        _rumbleDirty = false;
        if (strength <= 0f)
        {
            Raylib.SetGamepadVibration(_rumbleGamepad, 0, 0, 0);
            return;
        }

        Raylib.SetGamepadVibration(
            _rumbleGamepad,
            RumbleLowMotorStrength * strength,
            RumbleHighMotorStrength * strength,
            RumbleRefreshDuration);
    }

    private void StopRumble()
    {
        if (_rumbleGamepad >= 0 && Raylib.IsGamepadAvailable(_rumbleGamepad))
        {
            Raylib.SetGamepadVibration(_rumbleGamepad, 0, 0, 0);
        }

        _rumbleGamepad = -1;
    }

    private void ShowStatus(string status)
    {
        _statusText = status;
        _statusUntil = Raylib.GetTime() + 2;
        RefreshWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        if (_statusText.Length == 0 || Raylib.GetTime() < _statusUntil)
        {
            return;
        }

        _statusText = string.Empty;
        RefreshWindowTitle();
    }

    private void RefreshWindowTitle()
    {
        var title = _windowTitle;
        if (_paused)
        {
            title += " [PAUSED]";
        }

        if (_rewinding)
        {
            title += " [REWIND]";
        }
        else if (_fastForwarding)
        {
            title += $" [FAST FORWARD {FastForwardMultiplier}x]";
        }

        if (_statusText.Length > 0)
        {
            title += $" [{_statusText}]";
        }

        Raylib.SetWindowTitle(title);
    }

    private static int FindAvailableGamepad(int ordinal = 0)
    {
        var available = 0;
        for (var gamepad = 0; gamepad < MaxGamepads; gamepad++)
        {
            if (Raylib.IsGamepadAvailable(gamepad))
            {
                if (available == ordinal)
                {
                    return gamepad;
                }

                available++;
            }
        }

        return -1;
    }

    private static bool IsGamepadButtonPressed(int gamepad, GamepadButton button)
    {
        return gamepad >= 0 && Raylib.IsGamepadButtonPressed(gamepad, button);
    }

    private static bool IsGamepadButtonDown(int gamepad, GamepadButton button)
    {
        return gamepad >= 0 && Raylib.IsGamepadButtonDown(gamepad, button);
    }

    private void Draw(int scale)
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(RaylibColor.Black);
        Raylib.DrawTexturePro(
            _texture,
            new Rectangle(0, 0, _videoWidth, _videoHeight),
            new Rectangle(0, 0, _videoWidth * scale, _videoHeight * scale),
            Vector2.Zero,
            0,
            RaylibColor.White);
        _spatialFilterOverlays?.Draw();
        Raylib.EndDrawing();
    }
}
