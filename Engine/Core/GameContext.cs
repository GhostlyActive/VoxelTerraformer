using Raylib_cs;
using VoxelEngine.Audio;
using VoxelEngine.Config;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace VoxelEngine.Core;

/// <summary>
/// Everything a game gets from the engine: the shared tuning values, its sounds, where its assets
/// and saves live, the frame profiler and a couple of callbacks into the host.
/// </summary>
public sealed class GameContext
{
    private readonly GameHost _host;

    internal GameContext(GameHost host, GameEntry entry, EngineSettings settings, AudioBank audio,
        UserDataPaths paths, SettingsStore store, TuningMenu tuning, FrameProfiler profiler, bool benchmark)
    {
        _host = host;
        Id = entry.Id;
        Title = entry.Title;
        Settings = settings;
        Audio = audio;
        Paths = paths;
        Store = store;
        Tuning = tuning;
        Profiler = profiler;
        Benchmark = benchmark;
    }

    /// <summary>Folder name under <c>Games/</c>, which is where the game's assets are looked up</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>The engine's own dials: mouse, field of view, view distance</summary>
    public EngineSettings Settings { get; }

    public AudioBank Audio { get; }

    /// <summary>Saved sections of tuning values; a game keeps its own under a name of its choice</summary>
    public SettingsStore Store { get; }

    /// <summary>The menu on M; add a section for the game's own dials in Load and remove it in Unload</summary>
    public TuningMenu Tuning { get; }

    /// <summary>The product's folders in the user profile: settings and save slots</summary>
    public UserDataPaths Paths { get; }

    /// <summary>Main-thread timings of the current frame, for the debug overlay and benchmarks</summary>
    public FrameProfiler Profiler { get; }

    /// <summary>Started with --bench: the game may run a scripted measurement instead of waiting for input</summary>
    public bool Benchmark { get; }

    public int ScreenWidth => Raylib.GetScreenWidth();
    public int ScreenHeight => Raylib.GetScreenHeight();

    /// <summary>Debug overlay (F3); games hang their extra readouts off it</summary>
    public bool DebugOverlay => _host.DebugOverlay;

    /// <summary>Path to a file under <c>Games/&lt;Id&gt;/Assets/</c> in the output folder</summary>
    public string AssetPath(string relativePath) => Path.Combine(AssetRoot(Id), relativePath);

    internal static string AssetRoot(string gameId) => Path.Combine(AppContext.BaseDirectory, "Games", gameId, "Assets");

    /// <summary>A save slot of this product; every game picks a name of its own. The height has to match the world's.</summary>
    public WorldStorage OpenStorage(string slot, int worldHeight = VoxelWorld.DefaultHeight)
        => new(Paths.SaveDirectory(slot), worldHeight);

    /// <summary>Short message at the bottom centre; fades out on its own</summary>
    public void ShowStatus(string text) => _host.ShowStatus(text);

    /// <summary>Leave this game for another one at the end of the frame</summary>
    public void RequestGame(string id) => _host.RequestGame(id);

    /// <summary>Close the window at the end of the frame</summary>
    public void RequestQuit() => _host.RequestQuit();
}
