using Raylib_cs;

namespace VoxelEngine.Core;

/// <summary>
/// Basisklasse für ein Spiel. Der <see cref="GameHost"/> besitzt Fenster, Schleife und Menüs
/// und ruft hier der Reihe nach <see cref="Load"/>, pro Bild <see cref="Update"/> und die
/// Draw-Methoden, am Ende <see cref="Unload"/>.
///
/// Die Aufteilung des Zeichnens folgt den Raylib-Modi: <see cref="DrawBackground"/> läuft in 2D
/// vor der Szene, <see cref="DrawWorld"/> innerhalb von BeginMode3D, <see cref="DrawHud"/>
/// wieder in 2D darüber. Menüs zeichnet der Host obendrauf.
///
/// Der Konstruktor muss billig bleiben: die Registry legt zum Anzeigen der Liste eine Instanz an,
/// bevor klar ist, ob das Spiel überhaupt gestartet wird. Alles Teure gehört in <see cref="Load"/>.
/// </summary>
public abstract class Game
{
    /// <summary>Zugriff auf Engine-Dienste; steht ab <see cref="Load"/> bereit</summary>
    protected GameContext Context { get; private set; } = null!;

    /// <summary>Kamera, aus der die Szene gezeichnet wird</summary>
    public abstract Camera3D Camera { get; }

    /// <summary>Blendet "Save world" und "Load world" im Pausenmenü ein</summary>
    public virtual bool SupportsSaving => false;

    /// <summary>Tastenbelegung fürs Pausenmenü, eine Zeile pro Eintrag</summary>
    public virtual IReadOnlyList<string> ControlHints => Array.Empty<string>();

    internal void Attach(GameContext context) => Context = context;

    /// <summary>Welt aufbauen, Assets laden. Läuft einmal beim Wechsel in dieses Spiel.</summary>
    public abstract void Load();

    /// <summary>Ein Spielschritt. Läuft nicht, solange ein Menü offen ist.</summary>
    public abstract void Update(float dt);

    /// <summary>
    /// Läuft auch bei offenem Menü. Für Arbeit, die nicht stehenbleiben darf — etwa das
    /// Hochladen fertiger Chunk-Meshes aus den Worker-Threads.
    /// </summary>
    public virtual void UpdateWhilePaused() { }

    /// <summary>Himmel/Hintergrund, gezeichnet vor der Szene</summary>
    public virtual void DrawBackground() => Raylib.ClearBackground(Color.Black);

    /// <summary>Die 3D-Szene</summary>
    public abstract void DrawWorld();

    /// <summary>Anzeige über der Szene</summary>
    public virtual void DrawHud() { }

    /// <summary>Spielstand schreiben; nur aufgerufen, wenn <see cref="SupportsSaving"/> gilt</summary>
    public virtual bool SaveGame() => false;

    /// <summary>Spielstand laden; nur aufgerufen, wenn <see cref="SupportsSaving"/> gilt</summary>
    public virtual bool LoadGame() => false;

    /// <summary>Alles freigeben, was <see cref="Load"/> angelegt hat (Meshes, Shader, Sounds)</summary>
    public virtual void Unload() { }
}
