using System.Text.Json;

namespace VoxelEngine.Config;

/// <summary>
/// Die Regler aus dem Tuning-Menü (Taste M) — geteilt von allen Spielen, damit sich Maus,
/// Tempo und Sicht überall gleich anfühlen. Werden pro Rechner im Benutzerprofil gespeichert
/// (AppData bzw. ~/.config), damit sie Neustarts überleben.
/// </summary>
public sealed class EngineSettings
{
    public float MouseSensitivity { get; set; } = 0.12f;
    public float WalkSpeed { get; set; } = 7.0f;
    public float SprintMultiplier { get; set; } = 1.6f;
    public float JumpSpeed { get; set; } = 10.2f;
    public float Gravity { get; set; } = 18.0f;

    /// <summary>Reichweite für Abbauen und Bauen; ohne Treffer entsteht der Block frei in der Luft auf dieser Distanz</summary>
    public float BuildReach { get; set; } = 8f;

    /// <summary>Kugelradius des Sculpt-Brushes (Taste V, Ctrl+Mausrad)</summary>
    public float SculptRadius { get; set; } = 0.7f;

    /// <summary>Weiche Pinselflanke als Vielfaches des Radius (nur Smooth-Modus): 0 = harte Kante, größer = rundere Blobs</summary>
    public float BrushSoftness { get; set; } = 0.6f;

    /// <summary>Tempo der Baufront in Metern pro Sekunde (Rechtsklick). Klein = langsam wachsend und gut dosierbar.</summary>
    public float BuildSpeed { get; set; } = 2.5f;

    /// <summary>Wie lange Pinselkugel bzw. Blockrahmen nach einer Größenänderung sichtbar bleiben (Sekunden)</summary>
    public float PreviewHold { get; set; } = 1.0f;

    public float FieldOfView { get; set; } = 60f;

    /// <summary>Länge eines vollen Tag-Nacht-Zyklus in Sekunden</summary>
    public float DayLengthSeconds { get; set; } = 240f;

    /// <summary>Startzeit und Sprungziel der Sonne in Stunden (12 = Mittag)</summary>
    public float TimeOfDay { get; set; } = 12f;

    /// <summary>Tempo des Tageslaufs; 0 hält die Sonne an (auch über Z/U im Spiel)</summary>
    public float TimeFlow { get; set; } = 1f;

    public float FogStart { get; set; } = 100f;
    public float FogEnd { get; set; } = 230f;

    /// <summary>Anteil der Himmelszellen, die eine Wolke tragen</summary>
    public float CloudCoverage { get; set; } = 0.35f;
    public float CloudHeight { get; set; } = 80f;
    public float CloudDrift { get; set; } = 1.2f;

    // Dateiname aus der Zeit vor der Engine-Trennung — beibehalten, damit vorhandene
    // Einstellungen nicht stillschweigend auf die Defaults zurückfallen
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Terraformer", "debug-settings.json");

    public static EngineSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                EngineSettings? loaded = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unlesbare/kaputte Datei → mit Defaults weiterspielen statt crashen
        }

        return new EngineSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Speichern ist Komfort — das Spiel läuft auch ohne weiter
        }
    }

}
