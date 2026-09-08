using Raylib_cs;
using VoxelEngine.Audio;
using VoxelEngine.Config;
using VoxelEngine.Media;
using VoxelEngine.Rendering;
using VoxelEngine.UI;

namespace VoxelEngine.Core;

/// <summary>Window size, title and frame rate: everything that must be fixed before the first frame</summary>
public sealed record HostOptions
{
    /// <summary>Names the folder for settings and saves in the user profile</summary>
    public string ProductName { get; init; } = "VoxelEngine";

    public string WindowTitle { get; init; } = "VoxelEngine";

    /// <summary>&gt; 0: drop a screenshot after that many frames and quit (smoke test)</summary>
    public int SmokeFrames { get; init; }

    /// <summary>Scripted measurement run: the game drives itself and quits when done</summary>
    public bool Benchmark { get; init; }
}

/// <summary>
/// Owns the window, the main loop, the menus and the running game. Games know nothing about each
/// other: switching happens only here, and only between two frames — the old game releases
/// everything first, then the new one builds up.
/// </summary>
public sealed class GameHost : IDisposable
{
    private readonly GameRegistry _registry;
    private readonly HostOptions _options;

    private EngineSettings _settings = new();
    private UserDataPaths _paths = null!;
    private SettingsStore _store = null!;
    private PauseMenu _pauseMenu = null!;
    private TuningMenu _tuningMenu = null!;

    private Game? _game;
    private AudioBank? _audio;
    private string? _pendingGameId;

    private string _statusText = "";
    private float _statusTimer;

    private bool _quitRequested;
    private bool _cursorFree;

    /// <summary>Debug overlay on F3; games read it through the <see cref="GameContext"/></summary>
    public bool DebugOverlay { get; private set; }

    private readonly FrameStats _frameStats = new();
    private readonly FrameProfiler _profiler = new();
    private readonly ScreenRecorder _recorder = new();

    /// <summary>A frame longer than this is treated as a stall: the simulation steps at most this far</summary>
    private const float MaxFrameSeconds = 0.1f;

    internal void RequestGame(string id) => _pendingGameId = id;

    internal void RequestQuit() => _quitRequested = true;

    public GameHost(GameRegistry registry, HostOptions? options = null)
    {
        _registry = registry;
        _options = options ?? new HostOptions();
    }

    public void ShowStatus(string text)
    {
        _statusText = text;
        _statusTimer = 2.5f;
    }

    /// <summary>Window sizes offered in the menu; the first is the default</summary>
    private static readonly (int Width, int Height)[] Resolutions =
    {
        (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160),
    };

    private static readonly string[] ResolutionLabels = Resolutions.Select(r => $"{r.Width} x {r.Height}").ToArray();

    /// <summary>Video heights offered for a recording; 0 keeps the size of the window</summary>
    private static readonly int[] RecordingHeights = { 0, 720, 1080, 1440, 2160 };

    private static readonly string[] RecordingHeightLabels =
    {
        "as shown", "720p", "1080p", "1440p", "2160p",
    };

    private static readonly int[] RecordingRates = { 30, 60 };

    private static readonly string[] RecordingRateLabels = { "30 fps", "60 fps" };

    public void Run(string startGameId)
    {
        bool smokeTest = _options.SmokeFrames > 0;

        _paths = new UserDataPaths(_options.ProductName);
        _store = new SettingsStore(_paths.SettingsFile);
        _settings = _store.Load<EngineSettings>("Engine");

        // Multisampling and vsync are decided before the window exists. At this view distance a
        // distant block is a pixel or two wide, and without MSAA those edges crawl as soon as the
        // player moves. A change in the menu therefore waits for the next start.
        ConfigFlags flags = 0;
        if (_settings.Msaa) flags |= ConfigFlags.Msaa4xHint;
        if (_settings.VSync) flags |= ConfigFlags.VSyncHint;
        if (flags != 0) Raylib.SetConfigFlags(flags);

        Raylib.InitWindow(_settings.WindowWidth, _settings.WindowHeight, _options.WindowTitle);
        if (_settings.Fullscreen && !smokeTest) Raylib.ToggleBorderlessWindowed();
        Raylib.SetTargetFPS(_settings.TargetFps);
        Raylib.SetExitKey(KeyboardKey.Null); // ESC belongs to the pause menu, not to the window
        Raylib.InitAudioDevice();

        if (smokeTest) Raylib.SetMousePosition(_settings.WindowWidth / 2, _settings.WindowHeight / 2); // or the first mouse delta twists the camera
        else Raylib.DisableCursor();

        _pauseMenu = new PauseMenu(_registry);

        // What applies to every game sits in the pause menu, under Settings; the tuning menu on
        // M is left to the dials of the running scene and game
        var defaults = new EngineSettings();
        _pauseMenu.Settings.AddSection("DISPLAY", () => _store.Save("Engine", _settings))
            .Choice("Resolution", ResolutionLabels, ResolutionIndex, SetResolution, 0)
            .Toggle("Fullscreen", () => _settings.Fullscreen, v => _settings.Fullscreen = v, defaults.Fullscreen)
            .Toggle("VSync", () => _settings.VSync, v => _settings.VSync = v, defaults.VSync)
            .Value("Target FPS", () => _settings.TargetFps, v => _settings.TargetFps = (int)v, defaults.TargetFps, 10f, 30f, 240f, "0")
            .Toggle("MSAA 4x (restart)", () => _settings.Msaa, v => _settings.Msaa = v, defaults.Msaa);

        _pauseMenu.Settings.AddSection("VIEW AND CONTROLS", () => _store.Save("Engine", _settings))
            .Value("Mouse sensitivity", () => _settings.MouseSensitivity, v => _settings.MouseSensitivity = v, defaults.MouseSensitivity, 0.01f, 0.02f, 0.50f, "0.00")
            .Value("Field of view", () => _settings.FieldOfView, v => _settings.FieldOfView = v, defaults.FieldOfView, 2f, 50f, 110f, "0")
            .Value("View distance chunks", () => _settings.ViewDistanceChunks, v => _settings.ViewDistanceChunks = (int)v, defaults.ViewDistanceChunks, 2f, 6f, World.VoxelWorld.MaxViewDistance, "0")
            .Value("Detail radius chunks", () => _settings.DetailRadiusChunks, v => _settings.DetailRadiusChunks = (int)v, defaults.DetailRadiusChunks, 1f, 2f, World.VoxelWorld.MaxViewDistance, "0");

        _pauseMenu.Settings.AddSection("INTERFACE AND RECORDING", () => _store.Save("Engine", _settings))
            .Toggle("In-game text and readouts", () => _settings.ShowHud, v => _settings.ShowHud = v, defaults.ShowHud)
            .Choice("Video resolution", RecordingHeightLabels, RecordingHeightIndex, SetRecordingHeight, 2)
            .Choice("Video frame rate", RecordingRateLabels, RecordingRateIndex, SetRecordingRate, 1);

        _tuningMenu = new TuningMenu();
        _cursorFree = smokeTest;
        SetDebugOverlay(smokeTest); // on right away in the smoke test, so the stats end up on the screenshot

        SwitchTo(startGameId);

        int frames = 0;

        while (!Raylib.WindowShouldClose() && !_quitRequested)
        {
            float frameTime = Raylib.GetFrameTime();
            _frameStats.Add(frameTime);
            _profiler.BeginFrame();

            // A stalled frame (window drag, a load hitch) must not fling the player through the terrain
            float dt = MathF.Min(frameTime, MaxFrameSeconds);
            _statusTimer = MathF.Max(0f, _statusTimer - dt);

            // If the menu was open at the start of the frame, the game gets no input this frame:
            // otherwise the confirming Enter leaks straight into the gameplay
            bool menuWasOpen = _pauseMenu.IsOpen;
            HandleMenu();

            if (_pendingGameId != null)
            {
                SwitchTo(_pendingGameId);
                _pendingGameId = null;
            }

            bool paused = _pauseMenu.IsOpen || menuWasOpen;
            UpdateCursor(paused, smokeTest);
            if (!smokeTest) ApplyDisplaySettings();

            if (!paused)
            {
                if (Raylib.IsKeyPressed(KeyboardKey.F3)) SetDebugOverlay(!DebugOverlay);
                if (Raylib.IsKeyPressed(KeyboardKey.K)) ShowStatus(_recorder.Toggle(_settings.RecordingHeight, _settings.RecordingFps));

                _tuningMenu.Update();
                _game!.Update(dt);
            }

            // Deliberately after the gameplay step: this frame's edits are queued for meshing
            // right away instead of waiting for the next frame, which halves the delay between
            // carving something and seeing it
            _game!.UpdateAlways();

            Raylib.BeginDrawing();

            _game.DrawBackground();

            long drawStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Raylib.BeginMode3D(_game.Camera);
            _game.DrawWorld();
            Raylib.EndMode3D();
            _profiler.Add(FrameSlot.Draw, drawStarted);

            if (_settings.ShowHud)
            {
                _game.DrawHud();

                if (DebugOverlay)
                {
                    Raylib.DrawText(
                        $"{_frameStats.AverageMs:F2} ms | peak {_frameStats.PeakMs:F2} ms | {_frameStats.Fps} FPS",
                        10, 10, 20, Color.Green);
                    Raylib.DrawText(_profiler.Summary(), 10, Raylib.GetScreenHeight() - 24, 14, Color.Green);
                }
            }

            _tuningMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
            _pauseMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());

            // The frame goes into the video here, so what follows is for the player only: the
            // badge that says a recording is running, and the engine's own status line
            _recorder.CaptureFrame(frameTime);

            DrawRecordingBadge();
            DrawStatus();

            Raylib.EndDrawing();

            if (smokeTest && ++frames >= _options.SmokeFrames)
            {
                Raylib.TakeScreenshot("smoke.png");
                Console.WriteLine($"[smoke] last second: avg={_frameStats.AverageMs:F2}ms peak={_frameStats.PeakMs:F2}ms fps={_frameStats.Fps}");
                Console.WriteLine($"[smoke] whole run: {_frameStats.LifetimeReport()}");
                Console.WriteLine(_game.DebugReport());
                break;
            }
        }

        // The smoke test must not touch the tuning values: the window grabs focus on startup, so
        // stray key presses would otherwise end up in the config for good
        if (!smokeTest) _store.Save("Engine", _settings);
    }

    /// <summary>
    /// The debug overlay lifts the frame cap along with it. Capped, every frame reads as the cap
    /// no matter what the engine costs, and the timings next to it would say nothing at all.
    /// </summary>
    private void SetDebugOverlay(bool on)
    {
        DebugOverlay = on;
        Raylib.SetTargetFPS(on ? 0 : _settings.TargetFps);
    }

    private int _appliedTargetFps = -1;

    /// <summary>Window size, fullscreen, vsync and the frame cap follow the settings as they change</summary>
    private void ApplyDisplaySettings()
    {
        bool fullscreen = Raylib.IsWindowState(ConfigFlags.BorderlessWindowMode);
        if (_settings.Fullscreen != fullscreen) Raylib.ToggleBorderlessWindowed();

        if (!_settings.Fullscreen &&
            (Raylib.GetScreenWidth() != _settings.WindowWidth || Raylib.GetScreenHeight() != _settings.WindowHeight))
            Raylib.SetWindowSize(_settings.WindowWidth, _settings.WindowHeight);

        bool vsync = Raylib.IsWindowState(ConfigFlags.VSyncHint);
        if (_settings.VSync && !vsync) Raylib.SetWindowState(ConfigFlags.VSyncHint);
        else if (!_settings.VSync && vsync) Raylib.ClearWindowState(ConfigFlags.VSyncHint);

        if (_settings.TargetFps != _appliedTargetFps)
        {
            _appliedTargetFps = _settings.TargetFps;
            if (!DebugOverlay) Raylib.SetTargetFPS(_settings.TargetFps);
        }
    }

    private int ResolutionIndex()
    {
        for (int i = 0; i < Resolutions.Length; i++)
            if (Resolutions[i].Width == _settings.WindowWidth && Resolutions[i].Height == _settings.WindowHeight) return i;

        return 0;
    }

    private void SetResolution(int index)
    {
        (int width, int height) = Resolutions[Math.Clamp(index, 0, Resolutions.Length - 1)];
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
    }

    private int RecordingHeightIndex() => Math.Max(0, Array.IndexOf(RecordingHeights, _settings.RecordingHeight));

    private void SetRecordingHeight(int index)
        => _settings.RecordingHeight = RecordingHeights[Math.Clamp(index, 0, RecordingHeights.Length - 1)];

    private int RecordingRateIndex() => Math.Max(0, Array.IndexOf(RecordingRates, _settings.RecordingFps));

    private void SetRecordingRate(int index)
        => _settings.RecordingFps = RecordingRates[Math.Clamp(index, 0, RecordingRates.Length - 1)];

    private void HandleMenu()
    {
        PauseResult result = _pauseMenu.Update();

        switch (result.Action)
        {
            case PauseAction.Save:
                if (_game!.SaveGame())
                {
                    _pauseMenu.Close();
                    ShowStatus("World saved");
                }
                else ShowStatus("Save failed!");
                break;

            case PauseAction.Load:
                if (_game!.LoadGame())
                {
                    _pauseMenu.Close();
                    ShowStatus("World loaded");
                }
                else ShowStatus("No compatible save found");
                break;

            case PauseAction.StartGame:
                _pendingGameId = result.GameId;
                break;

            case PauseAction.Quit:
                _quitRequested = true;
                break;
        }
    }

    private void SwitchTo(string gameId)
    {
        GameEntry entry = _registry.Find(gameId);

        _game?.Unload();
        _audio?.Dispose();

        _audio = new AudioBank(Path.Combine(GameContext.AssetRoot(entry.Id), "Sounds"));
        _game = entry.Create();
        _game.Attach(new GameContext(this, entry, _settings, _audio, _paths, _store, _tuningMenu, _profiler, _options.Benchmark));
        _game.Load();

        // Raylib's defaults waste the depth buffer on the first centimetres, which flickers on
        // distant terrain; the game says how far its world reaches
        (double near, double far) = _game.ClipPlanes;
        Rlgl.SetClipPlanes(near, far);

        _pauseMenu.CurrentGameId = entry.Id;
        _pauseMenu.SavingAvailable = _game.SupportsSaving;
        _pauseMenu.ControlHints = _game.ControlHints;

        ShowStatus(entry.Title);
    }

    private void UpdateCursor(bool paused, bool smokeTest)
    {
        if (smokeTest) return;

        if (paused && !_cursorFree)
        {
            Raylib.EnableCursor();
            _cursorFree = true;
        }
        else if (!paused && _cursorFree)
        {
            Raylib.DisableCursor();
            _cursorFree = false;
        }
    }

    /// <summary>
    /// A pulsing dot and the running time while a recording is on. Drawn after the frame was
    /// handed to the recorder, so it marks the screen without ever landing in the video.
    /// </summary>
    private void DrawRecordingBadge()
    {
        if (!_recorder.IsRecording) return;

        int seconds = (int)_recorder.ElapsedSeconds;
        string label = $"REC  {seconds / 60:00}:{seconds % 60:00}";

        int textWidth = Raylib.MeasureText(label, 18);
        int x = Raylib.GetScreenWidth() - 20 - textWidth;

        float pulse = 0.55f + 0.45f * MathF.Sin(_recorder.ElapsedSeconds * 5f);
        Raylib.DrawCircle(x - 16, 27, 6f, new Color((byte)255, (byte)70, (byte)70, (byte)(140 + 115 * pulse)));
        Hud.Text(label, x, 18, 18, new Color(255, 210, 210, 255));
    }

    private void DrawStatus()
    {
        if (_statusTimer <= 0f) return;

        Hud.Centered(_statusText, Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() - 130, 20,
            new Color(140, 240, 160, 255));
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _game?.Unload();
        _audio?.Dispose();

        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
    }
}
