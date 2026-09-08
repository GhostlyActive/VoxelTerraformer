namespace VoxelEngine.Config;

/// <summary>
/// The dials of a terrain scene: pace, jump, the sculpting tool, the day, the fog and the clouds.
/// Owned by the scene and saved per game, so a game that pins its sun does not change the sun
/// of another.
/// </summary>
public sealed class TerrainSettings
{
    public float WalkSpeed { get; set; } = 7.0f;
    public float SprintMultiplier { get; set; } = 1.6f;
    public float JumpSpeed { get; set; } = 10.2f;
    public float Gravity { get; set; } = 18.0f;

    /// <summary>Lift of the jetpack in m/s², against gravity; only games that switch the jetpack on use it</summary>
    public float JetpackThrust { get; set; } = 32f;

    /// <summary>Seconds of burn in a full tank</summary>
    public float JetpackFuelSeconds { get; set; } = 2.5f;

    /// <summary>Metres per second in godmode's free flight; Shift multiplies it by the sprint factor</summary>
    public float FlySpeed { get; set; } = 26f;

    /// <summary>Reach for removing and placing; with nothing in the way the block appears in mid-air at this distance</summary>
    public float BuildReach { get; set; } = 8f;

    /// <summary>Sphere radius of the sculpt brush (Ctrl+wheel)</summary>
    public float SculptRadius { get; set; } = 0.7f;

    /// <summary>Soft brush falloff as a multiple of the radius (Smooth mode only): 0 = hard edge, larger = rounder blobs</summary>
    public float BrushSoftness { get; set; } = 0.6f;

    /// <summary>How long the brush sphere or block outline stays at full strength after a size change (seconds)</summary>
    public float PreviewHold { get; set; } = 1.0f;

    /// <summary>Show the brush sphere at rest in Sculpt and Smooth mode, not only after a size change</summary>
    public bool ShowBrushAlways { get; set; }

    /// <summary>Strength of the surface grid Sculpt mode draws near the camera; 0 hides it</summary>
    public float SculptGridStrength { get; set; } = 1f;

    /// <summary>Length of a full day/night cycle in seconds</summary>
    public float DayLengthSeconds { get; set; } = 240f;

    /// <summary>Where the sun stands, in degrees: 0 = sunrise, 90 = noon, 180 = sunset</summary>
    public float SunAngle { get; set; } = 90f;

    /// <summary>Speed of the day cycle; 0 stops the sun. Ignored by games that pin the sun.</summary>
    public float TimeFlow { get; set; } = 1f;

    public float FogStart { get; set; } = 560f;
    public float FogEnd { get; set; } = 940f;

    /// <summary>Share of the sky cells that carry a cloud</summary>
    public float CloudCoverage { get; set; } = 0.35f;
    public float CloudHeight { get; set; } = 220f;
    public float CloudDrift { get; set; } = 1.2f;
}
