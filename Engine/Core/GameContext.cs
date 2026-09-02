using Raylib_cs;
using VoxelEngine.Audio;
using VoxelEngine.Config;

namespace VoxelEngine.Core;

/// <summary>
/// Everything a game gets from the engine: the shared tuning values, its sounds, the path to its
/// assets and a couple of callbacks into the host.
/// </summary>
public sealed class GameContext
{
    private readonly GameHost _host;

    internal GameContext(GameHost host, GameEntry entry, EngineSettings settings, AudioBank audio)
    {
        _host = host;
        Id = entry.Id;
        Title = entry.Title;
        Settings = settings;
        Audio = audio;
    }

    /// <summary>Folder name under <c>Games/</c>, and with it the save slot</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>Shared tuning values from the menu on M</summary>
    public EngineSettings Settings { get; }

    public AudioBank Audio { get; }

    public int ScreenWidth => Raylib.GetScreenWidth();
    public int ScreenHeight => Raylib.GetScreenHeight();

    /// <summary>Debug overlay (F3); games hang their extra readouts off it</summary>
    public bool DebugOverlay => _host.DebugOverlay;

    /// <summary>Path to a file under <c>Games/&lt;Id&gt;/Assets/</c> in the output folder</summary>
    public string AssetPath(string relativePath)
        => Path.Combine(AppContext.BaseDirectory, "Games", Id, "Assets", relativePath);

    /// <summary>Short message at the bottom centre; fades out on its own</summary>
    public void ShowStatus(string text) => _host.ShowStatus(text);
}
