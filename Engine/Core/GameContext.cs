using Raylib_cs;
using VoxelEngine.Audio;
using VoxelEngine.Config;

namespace VoxelEngine.Core;

/// <summary>
/// Alles, was ein Spiel von der Engine bekommt: die geteilten Tuning-Werte, seine Sounds,
/// den Weg zu seinen Assets und ein paar Rückrufe an den Host.
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

    /// <summary>Ordnername unter <c>Games/</c>; zugleich der Spielstand-Slot</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>Geteilte Tuning-Werte aus dem Menü auf Taste M</summary>
    public EngineSettings Settings { get; }

    public AudioBank Audio { get; }

    public int ScreenWidth => Raylib.GetScreenWidth();
    public int ScreenHeight => Raylib.GetScreenHeight();

    /// <summary>Debug-Anzeige (F3) — Spiele blenden daran ihre Zusatzinfos ein</summary>
    public bool DebugOverlay => _host.DebugOverlay;

    /// <summary>Pfad zu einer Datei unter <c>Games/&lt;Id&gt;/Assets/</c> im Ausgabeordner</summary>
    public string AssetPath(string relativePath)
        => Path.Combine(AppContext.BaseDirectory, "Games", Id, "Assets", relativePath);

    /// <summary>Kurze Meldung mittig unten, verschwindet von selbst</summary>
    public void ShowStatus(string text) => _host.ShowStatus(text);
}
