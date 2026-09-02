using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Effects;
using VoxelEngine.Input;
using VoxelEngine.Rendering;
using VoxelEngine.World;

namespace VoxelEngine.Scenes;

/// <summary>Everything a <see cref="VoxelTerrainScene"/> needs to know before the first chunk exists</summary>
public sealed record VoxelTerrainOptions
{
    /// <summary>Save folder inside the user profile; one slot per game</summary>
    public string SaveSlot { get; init; } = "world";

    public Vector3 Spawn { get; init; } = new(128, 60, 128);

    /// <summary>Custom terrain; null falls back to <see cref="DefaultTerrainGenerator"/></summary>
    public ITerrainGenerator? Generator { get; init; }

    public TerrainMode Mode { get; init; } = TerrainMode.Blocks;

    /// <summary>May the player carve and add material with the mouse buttons?</summary>
    public bool AllowEditing { get; init; } = true;

    /// <summary>Let the sun travel, or pin it at <see cref="SunAngleDegrees"/></summary>
    public bool RunDayNight { get; init; } = true;

    /// <summary>Sun position on its arc: 0 = sunrise, 90 = noon, 180 = sunset</summary>
    public float SunAngleDegrees { get; init; } = 90f;

    public bool Clouds { get; init; } = true;
    public bool Stars { get; init; } = true;
    public bool SunAndMoon { get; init; } = true;

    /// <summary>Chunk radius loaded up front; without it the player drops through empty space</summary>
    public int PreloadRadius { get; init; } = 3;
}

/// <summary>
/// A voxel world with everything already wired up: streaming, background meshing, the terrain
/// shader, day cycle, sky, particles and a player with sub-voxel collision. A game sets spawn,
/// terrain and mode, then calls <see cref="Update"/>, <see cref="DrawBackground"/> and
/// <see cref="Draw"/> once per frame.
/// </summary>
public sealed class VoxelTerrainScene : IDisposable
{
    private readonly EngineSettings _settings;
    private readonly TerrainShader _shader;
    private readonly StarField? _stars;
    private readonly CloudLayer? _clouds;

    // The tuning menu (M) and the in-game keys (Z/U) both reach for the sun. Comparing against
    // these mirrors each frame tells us which side moved last.
    private float _menuSunAngle;
    private float _menuTimeFlow;

    private TerrainMode _mode;
    private float _shake;

    public VoxelWorld World { get; }
    public ChunkMeshManager Meshes { get; }
    public ParticleSystem Particles { get; }
    public PlayerController Player { get; }
    public DayNightCycle DayNight { get; }
    public WorldStorage Storage { get; }

    public Camera3D Camera { get; private set; }

    /// <summary>Seconds since the scene started; drives stars and drifting clouds</summary>
    public float ElapsedTime { get; private set; }

    /// <summary>Allow terraforming with the mouse buttons</summary>
    public bool AllowEditing { get; set; }

    /// <summary>
    /// Is the player in control? Turning this off freezes movement and mouse look while the
    /// world, its particles and the light keep running — for a death screen, for instance.
    /// </summary>
    public bool AllowPlayerControl { get; set; } = true;

    /// <summary>Show chunk bounds and the world grid (F3)</summary>
    public bool ShowDebugGeometry { get; set; }

    /// <summary>The three voxel modes; switching remeshes the world but never touches its data</summary>
    public TerrainMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            World.Mode = value;
            Meshes.SetSmoothRendering(value == TerrainMode.Smooth);
        }
    }

    public VoxelTerrainScene(EngineSettings settings, VoxelTerrainOptions options)
    {
        _settings = settings;

        Storage = new WorldStorage(options.SaveSlot);
        World = new VoxelWorld(Storage, options.Generator);
        Player = new PlayerController(options.Spawn, settings);

        // Load the spawn area up front so the player lands on solid ground
        World.EnsureAround(options.Spawn, options.PreloadRadius);

        DayNight = new DayNightCycle
        {
            Center = options.Spawn,
            DayLengthSeconds = settings.DayLengthSeconds,
            OrbitRadius = 300f,
            DrawSunAndMoon = options.SunAndMoon,
            AutoAdvance = options.RunDayNight,
            SunAngleDegrees = options.SunAngleDegrees,
            TimeScale = settings.TimeFlow,
        };

        // The game decides where its sun starts; the menu takes over from the first change on
        _menuSunAngle = settings.SunAngle;
        _menuTimeFlow = settings.TimeFlow;

        _shader = new TerrainShader();
        Meshes = new ChunkMeshManager(World);

        Particles = new ParticleSystem();
        World.BlockBroken += Particles.SpawnBlockBreak;
        World.BlockPlaced += Particles.SpawnBlockPlace;

        AllowEditing = options.AllowEditing;
        Mode = options.Mode;
        Meshes.BuildAllNow();

        _stars = options.Stars ? new StarField() : null;
        _clouds = options.Clouds ? new CloudLayer() : null;

        Camera = Player.CameraOnly();
    }

    /// <summary>Next voxel mode (Blocks → Sculpt → Smooth → Blocks)</summary>
    public void CycleMode() => Mode = (TerrainMode)(((int)Mode + 1) % 3);

    /// <summary>Shake the camera briefly, for instance after a nearby impact</summary>
    public void Shake(float strength) => _shake = MathF.Max(_shake, strength);

    public void Update(float dt)
    {
        ElapsedTime += dt;

        if (AllowPlayerControl) Camera = Player.Update(World, dt);

        // Stream the world around the player (budget: at most 2 new chunks per frame)
        World.UpdateStreaming(Player.Position, 2);

        UpdateDayNight(dt);

        World.ShowPreviewAlways = ShowDebugGeometry;
        if (AllowEditing && AllowPlayerControl)
        {
            UpdateToolSize();
            World.Update(Camera, Player.Bounds, _settings);
        }

        // Particles run through the unlit default shader, so the world light is baked in on spawn
        Vector3 light = DayNight.AmbientColor + DayNight.SunlightColor * 0.8f;
        Particles.LightScale = Math.Clamp((light.X + light.Y + light.Z) / 3f, 0.15f, 1.1f);
        Particles.Update(World, dt);

        ApplyShake(dt);
    }

    /// <summary>
    /// Upload finished meshes from the worker threads. Has to run while a menu is open too, or
    /// the world stays half-built after a mode switch.
    /// </summary>
    public void PumpMeshUploads() => Meshes.Update();

    /// <summary>Mouse wheel sets the build reach, Ctrl+wheel the radius of the sphere brush</summary>
    private void UpdateToolSize()
    {
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel == 0f) return;

        if (Raylib.IsKeyDown(KeyboardKey.LeftControl))
            _settings.SculptRadius = Math.Clamp(_settings.SculptRadius + wheel * 0.1f, 0.25f, 2.5f);
        else
            _settings.BuildReach = Math.Clamp(_settings.BuildReach + wheel, 2f, 60f);

        World.PulsePreview(); // flash the preview so the new size is visible
    }

    private void UpdateDayNight(float dt)
    {
        // Sun and moon travel with the player, which makes them read as infinitely far away
        DayNight.Center = Player.Position;
        DayNight.DayLengthSeconds = _settings.DayLengthSeconds;

        if (_settings.SunAngle != _menuSunAngle) DayNight.SunAngleDegrees = _settings.SunAngle;
        if (_settings.TimeFlow != _menuTimeFlow) DayNight.TimeScale = _settings.TimeFlow;

        DayNight.Update(dt);

        // Mirror back what the game itself changed via Z/U: the clock speed while time runs,
        // the sun angle while it stands still
        _settings.TimeFlow = DayNight.TimeScale;
        if (!DayNight.AutoAdvance) _settings.SunAngle = DayNight.SunAngleDegrees;

        _menuSunAngle = _settings.SunAngle;
        _menuTimeFlow = _settings.TimeFlow;
    }

    private void ApplyShake(float dt)
    {
        if (_shake <= 0f) return;

        _shake = MathF.Max(0f, _shake - dt * 2.5f);

        var offset = new Vector3(
            Random.Shared.NextSingle() - 0.5f,
            Random.Shared.NextSingle() - 0.5f,
            Random.Shared.NextSingle() - 0.5f) * _shake * 0.6f;

        Camera3D shaken = Camera;
        shaken.Position += offset;
        shaken.Target += offset;
        Camera = shaken;
    }

    /// <summary>
    /// Blow a crater: a soft rim so the hollow stays round in Smooth mode as well, plus debris,
    /// fire and smoke at the point of impact.
    /// </summary>
    public void Explode(Vector3 center, float radius, float power = 1.35f)
    {
        Color debris = TerrainColors.ForBlock(
            BlockRegistry.Terrain, (int)center.X, (int)center.Y, (int)center.Z);

        Particles.SpawnExplosion(center, debris, power);

        // An explosion must not treat the player as an obstacle: pass an empty box instead of theirs
        var noBounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MaxValue));
        World.SculptBlob(center, radius, add: false, edge: radius * 0.36f, BlockRegistry.Stone, noBounds);
    }

    /// <summary>Top of the terrain at this XZ position (Y of the first free block above it)</summary>
    public float SurfaceHeight(float x, float z)
    {
        int blockX = (int)MathF.Floor(x);
        int blockZ = (int)MathF.Floor(z);

        for (int y = VoxelWorld.WorldHeight - 1; y > 0; y--)
            if (BlockRegistry.IsSolid(World.GetBlock(blockX, y, blockZ)))
                return y + 1f;

        return 1f;
    }

    public bool Save()
    {
        return World.SaveWorld() && Storage.SaveMeta(Player.Position, DayNight.TimeSeconds);
    }

    public bool Load()
    {
        if (!World.LoadWorld()) return false;

        if (Storage.TryLoadMeta(out Vector3 position, out float timeSeconds))
        {
            Player.Teleport(position);
            DayNight.TimeSeconds = timeSeconds;
        }

        World.EnsureAround(Player.Position, 3);
        return true;
    }

    public void DrawBackground()
    {
        // Sky as a vertical gradient: darker at the zenith, brighter at the horizon (= fog colour)
        Raylib.ClearBackground(DayNight.SkyZenithColor);
        Raylib.DrawRectangleGradientV(
            0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(),
            DayNight.SkyZenithColor, DayNight.SkyColor);
    }

    public void Draw()
    {
        _shader.FogStart = _settings.FogStart;
        _shader.FogEnd = MathF.Max(_settings.FogEnd, _settings.FogStart + 10f);
        _shader.SetFrame(DayNight, Camera.Position);

        Frustum frustum = Frustum.FromCamera(
            Camera, Raylib.GetScreenWidth() / (float)Raylib.GetScreenHeight());
        Meshes.Draw(_shader.Material, frustum);

        World.DrawHover();
        Particles.Draw();
        DayNight.Draw3D(Camera);

        _stars?.Draw(Camera, 1f - DayNight.Daylight01, ElapsedTime);

        if (_clouds != null)
        {
            _clouds.Coverage = _settings.CloudCoverage;
            _clouds.Height = _settings.CloudHeight;
            _clouds.DriftSpeed = _settings.CloudDrift;
            _clouds.Draw(Camera, ElapsedTime, DayNight.Daylight01, Player.Position);
        }

        if (!ShowDebugGeometry) return;

        GridRenderer.DrawFromOrigin(256, 1.0f);
        Meshes.DrawChunkBounds();
    }

    public void Dispose()
    {
        Meshes.Dispose();
        _shader.Unload();
    }
}
