using System.Text.Json;

namespace Terraformer.Config;

/// <summary>
/// Tuning-Werte für das Debug-Menü (Taste M). Werden pro Rechner im Benutzerprofil
/// gespeichert (AppData bzw. ~/.config), damit sie Neustarts überleben.
/// </summary>
public sealed class DebugSettings
{
    public float MouseSensitivity { get; set; } = 0.12f;
    public float WalkSpeed { get; set; } = 7.0f;
    public float SprintMultiplier { get; set; } = 1.6f;
    public float JumpSpeed { get; set; } = 10.2f;
    public float Gravity { get; set; } = 18.0f;

    /// <summary>Reichweite für Abbauen und Bauen; ohne Treffer entsteht der Block frei in der Luft auf dieser Distanz</summary>
    public float BuildReach { get; set; } = 8f;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Terraformer", "debug-settings.json");

    public static DebugSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                DebugSettings? loaded = JsonSerializer.Deserialize<DebugSettings>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unlesbare/kaputte Datei → mit Defaults weiterspielen statt crashen
        }

        return new DebugSettings();
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
