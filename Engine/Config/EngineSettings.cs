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

    /// <summary>How long the brush sphere or block outline stays visible after a size change (seconds)</summary>
    public float PreviewHold { get; set; } = 1.0f;

    public float FieldOfView { get; set; } = 60f;

    /// <summary>Length of a full day/night cycle in seconds</summary>
    public float DayLengthSeconds { get; set; } = 240f;

    /// <summary>Where the sun stands, in degrees: 0 = sunrise, 90 = noon, 180 = sunset</summary>
    public float SunAngle { get; set; } = 90f;

    /// <summary>Speed of the day cycle; 0 stops the sun. Ignored by games that pin the sun.</summary>
    public float TimeFlow { get; set; } = 1f;

    public float FogStart { get; set; } = 560f;
    public float FogEnd { get; set; } = 940f;

    /// <summary>Radius in chunks the world streams around the player (32 blocks each)</summary>
    public int ViewDistanceChunks { get; set; } = 32;

    /// <summary>Radius in chunks meshed at full detail; beyond it the terrain drops to 2 m and then 4 m blocks</summary>
    public int DetailRadiusChunks { get; set; } = 8;

    /// <summary>Strength of the surface grid Sculpt mode draws near the camera; 0 hides it</summary>
    public float SculptGridStrength { get; set; } = 1f;

    /// <summary>Show the brush sphere at rest in Sculpt and Smooth mode, not only after a size change</summary>
    public bool ShowBrushAlways { get; set; } = true;

    /// <summary>Share of the sky cells that carry a cloud</summary>
    public float CloudCoverage { get; set; } = 0.35f;
    public float CloudHeight { get; set; } = 80f;
    public float CloudDrift { get; set; } = 1.2f;

    private string? _filePath;

    /// <summary>Reads the settings file, or starts from the defaults when there is none or it is broken</summary>
    public static EngineSettings Load(string filePath)
    {
        EngineSettings settings = new();

        try
        {
            if (File.Exists(filePath))
                settings = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(filePath)) ?? settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable or broken file: carry on with the defaults instead of crashing
        }

        settings._filePath = filePath;
        return settings;
    }

    /// <summary>Writes the settings back to the file they were loaded from; a no-op for settings that never had one</summary>
    public void Save()
    {
        if (_filePath == null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Saving is a convenience; the game runs fine without it
        }
    }
}
