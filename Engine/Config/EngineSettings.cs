namespace VoxelEngine.Config;

/// <summary>
/// The dials that belong to the engine itself, whatever game runs: how the mouse feels, how wide
/// the view is, how far the world streams. Everything about a particular world lives in
/// <see cref="TerrainSettings"/>, owned by the scene that shows it.
/// </summary>
public sealed class EngineSettings
{
    /// <summary>Window size in points; a change applies at once</summary>
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;

    /// <summary>Borderless window over the whole screen</summary>
    public bool Fullscreen { get; set; }

    /// <summary>4x multisampling; raylib decides this before the window exists, so it takes a restart</summary>
    public bool Msaa { get; set; } = true;

    public bool VSync { get; set; }

    /// <summary>Frame cap; the debug overlay lifts it while it is on</summary>
    public int TargetFps { get; set; } = 60;

    public float MouseSensitivity { get; set; } = 0.12f;

    public float FieldOfView { get; set; } = 60f;

    /// <summary>Radius in chunks the world streams around the player (32 blocks each)</summary>
    public int ViewDistanceChunks { get; set; } = 32;

    /// <summary>Radius in chunks meshed at full detail; beyond it the terrain drops to 2 m and then 4 m blocks</summary>
    public int DetailRadiusChunks { get; set; } = 8;
}
