using Raylib_cs;
using System.Numerics;
using System.Runtime.InteropServices;
using VoxelEngine.Audio;
using VoxelEngine.Config;
using VoxelEngine.Core;
using VoxelEngine.Effects;
using VoxelEngine.Input;
using VoxelEngine.Rendering;
using VoxelEngine.World;

namespace VoxelEngine.Scenes;

/// <summary>Everything a <see cref="VoxelTerrainScene"/> needs to know before the first chunk exists</summary>
public sealed record VoxelTerrainOptions
{
    /// <summary>Save slot of this game, resolved through the context's <see cref="Config.UserDataPaths"/></summary>
    public string SaveSlot { get; init; } = "world";

    /// <summary>Explicit storage instead of the slot; null without a context means the world cannot be saved</summary>
    public WorldStorage? Storage { get; init; }

    /// <summary>What the player places and sculpts with</summary>
    public byte BuildMaterial { get; init; } = BlockRegistry.Stone;

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
    private readonly FrameProfiler? _profiler;
    private readonly AudioBank? _audio;
    private readonly TerrainShader _shader;
    private readonly StarField? _stars;
    private readonly CloudLayer? _clouds;

    // The tuning menu (M) and the in-game keys (Z/U) both reach for the sun. Comparing against
    // these mirrors each frame tells us which side moved last.
    private float _menuSunAngle;
    private float _menuTimeFlow;

    private TerrainMode _mode;
    private float _shake;
    private float _sinceModeSwitch = 99f;

    // Local lights live here rather than in the games: an explosion should light its own crater
    // without every game rebuilding that machinery
    private readonly List<TransientLight> _lights = new();
    private readonly List<PointLight> _visibleLights = new();

    private struct TransientLight
    {
        public Vector3 Position;
        public float Range;
        public Vector3 Color;
        public float Life;
        public float MaxLife;
    }

    public VoxelWorld World { get; }
    public ChunkMeshManager Meshes { get; }
    public ParticleSystem Particles { get; }
    public PlayerController Player { get; }
    public DayNightCycle DayNight { get; }

    /// <summary>Null when the scene was created without a save slot; Save and Load then report failure</summary>
    public WorldStorage? Storage { get; }

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

    /// <summary>The wave of the last mode switch while it runs, or null</summary>
    public ModeTransition? Transition { get; private set; }

    /// <summary>Seconds since the last mode switch; large while none happened</summary>
    public float SinceModeSwitch => _sinceModeSwitch;

    /// <summary>Raised when the mode changes, with the old and the new one</summary>
    public event Action<TerrainMode, TerrainMode>? ModeChanged;

    /// <summary>The three voxel modes; switching remeshes the world but never touches its data</summary>
    public TerrainMode Mode
    {
        get => _mode;
        set
        {
            TerrainMode previous = _mode;
            bool changed = value != previous;

            _mode = value;
            World.Mode = value;
            World.CancelStroke();

            bool remeshes = (value == TerrainMode.Smooth) != Meshes.SmoothRendering;
            Meshes.SetSmoothRendering(value == TerrainMode.Smooth);

            // A switch on a world that is already standing gets its wave; the one from the
            // constructor, before anything is meshed, is just the starting mode
            if (!changed || Meshes.MeshedChunks == 0) return;

            Transition = new ModeTransition(previous, value, Player.Position, remeshes);
            _sinceModeSwitch = 0f;

            Particles.SpawnRing(Player.Position + Vector3.UnitY * 0.15f, ModeColors.Of(value));
            _audio?.Play("engine.mode", 0.5f, value switch { TerrainMode.Blocks => 0.75f, TerrainMode.Sculpt => 1.0f, _ => 1.25f });

            ModeChanged?.Invoke(previous, value);
        }
    }

    public VoxelTerrainScene(GameContext context, VoxelTerrainOptions options)
        : this(context.Settings, options with { Storage = options.Storage ?? context.OpenStorage(options.SaveSlot) },
            context.Profiler, context.Audio)
    {
    }

    public VoxelTerrainScene(EngineSettings settings, VoxelTerrainOptions options, FrameProfiler? profiler = null, AudioBank? audio = null)
    {
        _settings = settings;
        _profiler = profiler;
        _audio = audio;

        Storage = options.Storage;
        World = new VoxelWorld(Storage, options.Generator, settings.ViewDistanceChunks) { BuildMaterial = options.BuildMaterial };
        Player = new PlayerController(options.Spawn, settings);

        // The mesh manager listens for chunks becoming meshable, so it has to exist before the
        // first chunk does
        _shader = new TerrainShader();
        Meshes = new ChunkMeshManager(World);

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

        Particles = new ParticleSystem();
        World.BlockBroken += Particles.SpawnBlockBreak;
        World.BlockPlaced += Particles.SpawnBlockPlace;

        // Digging and building are heard, not only seen: the ear registers a stroke before the
        // mesh has caught up. Synthesized stand-ins, replaced by files a game ships under these names.
        if (_audio != null)
        {
            _audio.Define("engine.dig", new SfxShape(0.12f, 260f, 90f, 0.8f, 14f));
            _audio.Define("engine.place", new SfxShape(0.10f, 180f, 420f, 0.3f, 12f));
            _audio.Define("engine.mode", new SfxShape(0.35f, 320f, 900f, 0.05f, 6f));
            World.BlockBroken += (_, _) => _audio.Play("engine.dig", 0.35f, 0.9f + Random.Shared.NextSingle() * 0.2f);
            World.BlockPlaced += (_, _) => _audio.Play("engine.place", 0.3f, 0.95f + Random.Shared.NextSingle() * 0.1f);
        }

        AllowEditing = options.AllowEditing;
        Mode = options.Mode;

        Camera = Player.CameraOnly();
        Meshes.DetailRadius = settings.DetailRadiusChunks;
        Meshes.BuildAllNow(Camera.Position);

        // Just inside the far plane, so the stars hang behind even the largest view distance
        _stars = options.Stars ? new StarField(distance: Frustum.FarPlane - 100f) : null;
        _clouds = options.Clouds ? new CloudLayer() : null;
    }

    /// <summary>Next voxel mode (Blocks → Sculpt → Smooth → Blocks)</summary>
    public void CycleMode() => Mode = (TerrainMode)(((int)Mode + 1) % 3);

    /// <summary>Shake the camera briefly, for instance after a nearby impact</summary>
    public void Shake(float strength) => _shake = MathF.Max(_shake, strength);

    /// <summary>
    /// A local light that fades out by itself. Only the nearest
    /// <see cref="TerrainShader.MaxPointLights"/> reach the shader, so a busy moment keeps the ones
    /// the player is actually standing in.
    /// </summary>
    public void AddLight(Vector3 position, float range, Vector3 color, float seconds)
    {
        const int budget = 48;
        if (_lights.Count >= budget) _lights.RemoveAt(0);

        _lights.Add(new TransientLight
        {
            Position = position,
            Range = range,
            Color = color,
            Life = seconds,
            MaxLife = MathF.Max(seconds, 0.001f),
        });
    }

    public void Update(float dt)
    {
        ElapsedTime += dt;

        if (AllowPlayerControl) Camera = Player.Update(World, dt); else Camera = Player.CameraOnly();

        // Stream the world around the player; the chunks themselves are built on worker threads
        World.SetViewDistance(_settings.ViewDistanceChunks);
        Meshes.DetailRadius = _settings.DetailRadiusChunks;

        long streamStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        int loadedBefore = World.LoadedChunkCount;
        World.UpdateStreaming(Player.Position);
        _profiler?.Add(FrameSlot.Stream, streamStarted, Math.Max(0, World.LoadedChunkCount - loadedBefore));

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

        _sinceModeSwitch += dt;
        if (Transition != null)
        {
            float truth = Transition.Remeshes ? Meshes.UnconvertedRadius(Transition.Origin) : float.PositiveInfinity;
            Transition.Advance(dt, truth, FogEnd);
            if (Transition.Done) Transition = null;
        }

        for (int i = _lights.Count - 1; i >= 0; i--)
        {
            TransientLight flash = _lights[i];
            flash.Life -= dt;

            if (flash.Life <= 0f) _lights.RemoveAt(i);
            else _lights[i] = flash;
        }

        ApplyShake(dt);
    }

    /// <summary>
    /// Upload finished meshes from the worker threads. Has to run while a menu is open too, or
    /// the world stays half-built after a mode switch.
    /// </summary>
    public void PumpMeshUploads() => Meshes.Update(Camera.Position, _profiler);

    /// <summary>Furthest distance at which terrain is drawn: the fog end, capped just inside the streamed world</summary>
    public float FogEnd => MathF.Min(_settings.FogEnd, (World.LoadRadius - 1) * Chunk.Size);

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

        // The blast lights the crater it just dug
        AddLight(center, radius * 7f, new Vector3(1.6f, 0.9f, 0.4f), 0.55f);

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
        return Storage != null && World.SaveWorld() && Storage.SaveMeta(Player.Position, DayNight.TimeSeconds);
    }

    public bool Load()
    {
        if (Storage == null || !World.LoadWorld()) return false;

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
        float fogEnd = FogEnd;
        _shader.FogEnd = MathF.Max(fogEnd, 50f);
        _shader.FogStart = MathF.Min(_settings.FogStart, _shader.FogEnd - 10f);
        _shader.SetFrame(DayNight, Camera.Position);
        _shader.SetPointLights(NearestLights());
        _shader.SetGrid(SubVoxels.CellSize, Mode == TerrainMode.Sculpt ? _settings.SculptGridStrength : 0f);

        if (Transition is { } wave)
            _shader.SetWave(wave.Origin, wave.DisplayRadius, wave.Width, wave.Strength, ModeColors.Tint(wave.To));
        else
            _shader.SetWave(Vector3.Zero, 1e9f, 1f, 0f, Vector3.One);

        Frustum frustum = Frustum.FromCamera(
            Camera, Raylib.GetScreenWidth() / (float)Raylib.GetScreenHeight());
        Meshes.Draw(_shader.Shader, frustum, Camera.Position, fogEnd + Chunk.Size);

        World.DrawHover();
        Particles.Draw();
        DayNight.Draw3D(Camera);

        _stars?.Draw(Camera, 1f - DayNight.Daylight01, ElapsedTime);

        if (_clouds != null)
        {
            _clouds.Coverage = _settings.CloudCoverage;
            _clouds.Height = _settings.CloudHeight;
            _clouds.DriftSpeed = _settings.CloudDrift;
            _clouds.Range = fogEnd + 80f;
            _clouds.Draw(frustum, ElapsedTime, DayNight.Daylight01, Player.Position);
        }

        if (!ShowDebugGeometry) return;

        GridRenderer.DrawFromOrigin(256, 1.0f);
        Meshes.DrawChunkBounds();
    }

    /// <summary>The lights closest to the camera, dimmed by how much life they have left</summary>
    private ReadOnlySpan<PointLight> NearestLights()
    {
        _visibleLights.Clear();
        if (_lights.Count == 0) return ReadOnlySpan<PointLight>.Empty;

        foreach (TransientLight light in _lights)
            _visibleLights.Add(new PointLight(light.Position, light.Range, light.Color * (light.Life / light.MaxLife)));

        Vector3 eye = Camera.Position;
        _visibleLights.Sort((a, b) =>
            Vector3.DistanceSquared(a.Position, eye).CompareTo(Vector3.DistanceSquared(b.Position, eye)));

        int count = Math.Min(_visibleLights.Count, TerrainShader.MaxPointLights);
        return CollectionsMarshal.AsSpan(_visibleLights)[..count];
    }

    public void Dispose()
    {
        Meshes.Dispose();
        World.Dispose();
        _clouds?.Dispose();
        _shader.Unload();
    }
}
