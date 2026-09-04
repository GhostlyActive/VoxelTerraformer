using Raylib_cs;
using VoxelEngine.Audio;
using VoxelEngine.Config;
using VoxelEngine.Rendering;
using VoxelEngine.UI;

namespace VoxelEngine.Core;

/// <summary>Window size, title and frame rate: everything that must be fixed before the first frame</summary>
public sealed record HostOptions
{
    /// <summary>Names the folder for settings and saves in the user profile</summary>
    public string ProductName { get; init; } = "VoxelEngine";

    public string WindowTitle { get; init; } = "VoxelEngine";
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int TargetFps { get; init; } = 60;

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

    public void Run(string startGameId)
    {
        bool smokeTest = _options.SmokeFrames > 0;

        // Multisampling before the window exists. At this view distance a distant block is a pixel
        // or two wide, and without it those edges crawl as soon as the player moves.
        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint);

        Raylib.InitWindow(_options.Width, _options.Height, _options.WindowTitle);
        Raylib.SetTargetFPS(_options.TargetFps);
        Raylib.SetExitKey(KeyboardKey.Null); // ESC belongs to the pause menu, not to the window
        Raylib.InitAudioDevice();

        if (smokeTest) Raylib.SetMousePosition(_options.Width / 2, _options.Height / 2); // or the first mouse delta twists the camera
        else Raylib.DisableCursor();

        _paths = new UserDataPaths(_options.ProductName);
        _settings = EngineSettings.Load(_paths.SettingsFile);
        _pauseMenu = new PauseMenu(_registry);
        _tuningMenu = new TuningMenu(_settings);
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

            if (!paused)
            {
                if (Raylib.IsKeyPressed(KeyboardKey.F3)) SetDebugOverlay(!DebugOverlay);

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

            _game.DrawHud();

            if (DebugOverlay)
            {
                Raylib.DrawText(
                    $"{_frameStats.AverageMs:F2} ms | peak {_frameStats.PeakMs:F2} ms | {_frameStats.Fps} FPS",
                    10, 10, 20, Color.Green);
                Raylib.DrawText(_profiler.Summary(), 10, Raylib.GetScreenHeight() - 24, 14, Color.Green);
            }

            _tuningMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
            _pauseMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
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
        if (!smokeTest) _settings.Save();
    }

    /// <summary>
    /// The debug overlay lifts the frame cap along with it. Capped, every frame reads as the cap
    /// no matter what the engine costs, and the timings next to it would say nothing at all.
    /// </summary>
    private void SetDebugOverlay(bool on)
    {
        DebugOverlay = on;
        Raylib.SetTargetFPS(on ? 0 : _options.TargetFps);
    }

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
        _game.Attach(new GameContext(this, entry, _settings, _audio, _paths, _profiler, _options.Benchmark));
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

    private void DrawStatus()
    {
        if (_statusTimer <= 0f) return;

        Hud.Centered(_statusText, Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() - 130, 20,
            new Color(140, 240, 160, 255));
    }

    public void Dispose()
    {
        _game?.Unload();
        _audio?.Dispose();

        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
    }
}
