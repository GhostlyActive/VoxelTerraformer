using System.Text.Json;

namespace VoxelEngine.Config;

/// <summary>
/// The dials from the tuning menu (key M), shared by every game so that mouse, pace and view feel
/// the same everywhere. Stored per machine in the user profile (AppData or ~/.config) so they
/// survive restarts.
/// </summary>
public sealed class EngineSettings
{
    public float MouseSensitivity { get; set; } = 0.12f;
    public float WalkSpeed { get; set; } = 7.0f;
    public float SprintMultiplier { get; set; } = 1.6f;
    public float JumpSpeed { get; set; } = 10.2f;
    public float Gravity { get; set; } = 18.0f;

    /// <summary>Reach for removing and placing; with nothing in the way the block appears in mid-air at this distance</summary>
    public float BuildReach { get; set; } = 8f;

    /// <summary>Sphere radius of the sculpt brush (key V, Ctrl+wheel)</summary>
    public float SculptRadius { get; set; } = 0.7f;

    /// <summary>Soft brush falloff as a multiple of the radius (Smooth mode only): 0 = hard edge, larger = rounder blobs</summary>
    public float BrushSoftness { get; set; } = 0.6f;

    /// <summary>Speed of the build front in metres per second (right mouse). Low = grows slowly and stays controllable.</summary>
    public float BuildSpeed { get; set; } = 2.5f;

    /// <summary>How long the brush sphere or block outline stays visible after a size change (seconds)</summary>
    public float PreviewHold { get; set; } = 1.0f;

    public float FieldOfView { get; set; } = 60f;

    /// <summary>Length of a full day/night cycle in seconds</summary>
    public float DayLengthSeconds { get; set; } = 240f;

    /// <summary>Where the sun stands, in degrees: 0 = sunrise, 90 = noon, 180 = sunset</summary>
    public float SunAngle { get; set; } = 90f;

    /// <summary>Speed of the day cycle; 0 stops the sun. Ignored by games that pin the sun.</summary>
    public float TimeFlow { get; set; } = 1f;

    public float FogStart { get; set; } = 460f;
    public float FogEnd { get; set; } = 740f;

    /// <summary>Share of the sky cells that carry a cloud</summary>
    public float CloudCoverage { get; set; } = 0.35f;
    public float CloudHeight { get; set; } = 80f;
    public float CloudDrift { get; set; } = 1.2f;

    // File name from before the engine split; kept so existing settings do not silently fall
    // back to the defaults
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
            // Unreadable or broken file: carry on with the defaults instead of crashing
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
            // Saving is a convenience; the game runs fine without it
        }
    }

}
