using Raylib_cs;
using VoxelEngine.Audio;
using VoxelEngine.Config;
using VoxelEngine.UI;

namespace VoxelEngine.Core;

/// <summary>Fenstergröße, Titel und Bildrate — alles, was vor dem ersten Frame feststehen muss</summary>
public sealed record HostOptions
{
    public string WindowTitle { get; init; } = "VoxelEngine";
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int TargetFps { get; init; } = 60;

    /// <summary>&gt; 0: nach so vielen Bildern einen Screenshot ablegen und beenden (Rauchtest)</summary>
    public int SmokeFrames { get; init; }
}

/// <summary>
/// Besitzt Fenster, Hauptschleife, Menüs und das laufende Spiel. Spiele kennen einander nicht:
/// gewechselt wird ausschließlich hier, und zwar zwischen zwei Bildern — das alte Spiel gibt
/// erst alles frei, dann baut das neue auf.
/// </summary>
public sealed class GameHost : IDisposable
{
    private readonly GameRegistry _registry;
    private readonly HostOptions _options;

    private EngineSettings _settings = new();
    private PauseMenu _pauseMenu = null!;
    private TuningMenu _tuningMenu = null!;

    private Game? _game;
    private AudioBank? _audio;
    private string? _pendingGameId;

    private string _statusText = "";
    private float _statusTimer;

    private bool _quitRequested;
    private bool _cursorFree;

    /// <summary>Debug-Anzeige auf F3 — Spiele lesen das über den <see cref="GameContext"/></summary>
    public bool DebugOverlay { get; private set; }

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

        Raylib.InitWindow(_options.Width, _options.Height, _options.WindowTitle);
        Raylib.SetTargetFPS(_options.TargetFps);
        Raylib.SetExitKey(KeyboardKey.Null); // ESC gehört dem Pausenmenü, nicht dem Fenster
        Raylib.InitAudioDevice();

        if (smokeTest) Raylib.SetMousePosition(_options.Width / 2, _options.Height / 2); // sonst verdreht das erste Maus-Delta die Kamera
        else Raylib.DisableCursor();

        _settings = EngineSettings.Load();
        _pauseMenu = new PauseMenu(_registry);
        _tuningMenu = new TuningMenu(_settings);
        _cursorFree = smokeTest;
        DebugOverlay = smokeTest; // im Rauchtest direkt an, damit die Stats auf dem Screenshot stehen

        SwitchTo(startGameId);

        int frames = 0;

        while (!Raylib.WindowShouldClose() && !_quitRequested)
        {
            float dt = Raylib.GetFrameTime();
            _statusTimer = MathF.Max(0f, _statusTimer - dt);

            // War das Menü zu Bildbeginn offen, bekommt das Spiel diesen Frame keine Eingaben —
            // sonst leakt das bestätigende Enter direkt ins Gameplay
            bool menuWasOpen = _pauseMenu.IsOpen;
            HandleMenu();

            if (_pendingGameId != null)
            {
                SwitchTo(_pendingGameId);
                _pendingGameId = null;
            }

            bool paused = _pauseMenu.IsOpen || menuWasOpen;
            UpdateCursor(paused, smokeTest);

            _game!.UpdateWhilePaused();

            if (!paused)
            {
                if (Raylib.IsKeyPressed(KeyboardKey.F3)) DebugOverlay = !DebugOverlay;

                _tuningMenu.Update();
                _game.Update(dt);
            }

            Raylib.BeginDrawing();

            _game.DrawBackground();

            Raylib.BeginMode3D(_game.Camera);
            _game.DrawWorld();
            Raylib.EndMode3D();

            _game.DrawHud();

            if (DebugOverlay) Raylib.DrawFPS(10, 10);

            _tuningMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
            _pauseMenu.Draw(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
            DrawStatus();

            Raylib.EndDrawing();

            if (smokeTest && ++frames >= _options.SmokeFrames)
            {
                Raylib.TakeScreenshot("smoke.png");
                break;
            }
        }

        // Der Rauchtest darf die Tuning-Werte nicht anfassen: das Fenster reißt beim Start den
        // Fokus an sich, versehentliche Tasten landen sonst dauerhaft in der Config
        if (!smokeTest) _settings.Save();
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

        _audio = new AudioBank(Path.Combine(AppContext.BaseDirectory, "Games", entry.Id, "Assets", "Sounds"));
        _game = entry.Create();
        _game.Attach(new GameContext(this, entry, _settings, _audio));
        _game.Load();

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

        Hud.Centered(_statusText, Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() - 80, 20,
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
