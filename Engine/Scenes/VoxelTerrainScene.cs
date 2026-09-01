using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Effects;
using VoxelEngine.Input;
using VoxelEngine.Rendering;
using VoxelEngine.World;

namespace VoxelEngine.Scenes;

/// <summary>Aufbau einer <see cref="VoxelTerrainScene"/> — alles, was vor dem ersten Chunk feststehen muss</summary>
public sealed record VoxelTerrainOptions
{
    /// <summary>Unterordner des Spielstands im Benutzerprofil; ein Slot je Spiel</summary>
    public string SaveSlot { get; init; } = "world";

    public Vector3 Spawn { get; init; } = new(128, 60, 128);

    /// <summary>Eigenes Gelände; null nimmt <see cref="DefaultTerrainGenerator"/></summary>
    public ITerrainGenerator? Generator { get; init; }

    public TerrainMode Mode { get; init; } = TerrainMode.Blocks;

    /// <summary>Darf der Spieler mit Maustasten Material abtragen und auftragen?</summary>
    public bool AllowEditing { get; init; } = true;

    public bool Clouds { get; init; } = true;
    public bool Stars { get; init; } = true;
    public bool SunAndMoon { get; init; } = true;

    /// <summary>Chunk-Radius, der beim Start blockierend geladen wird — darunter fällt der Spieler ins Leere</summary>
    public int PreloadRadius { get; init; } = 3;
}

/// <summary>
/// Eine fertig verdrahtete Voxelwelt: Streaming, Meshing im Hintergrund, Terrain-Shader,
/// Tageslauf, Himmel, Partikel und ein Spieler mit Sub-Voxel-Kollision. Ein Spiel setzt
/// Spawn, Gelände und Modus und ruft pro Bild <see cref="Update"/>, <see cref="DrawBackground"/>
/// und <see cref="Draw"/> — der Rest hängt schon zusammen.
/// </summary>
public sealed class VoxelTerrainScene : IDisposable
{
    private readonly EngineSettings _settings;
    private readonly TerrainShader _shader;
    private readonly StarField? _stars;
    private readonly CloudLayer? _clouds;

    // Menü (Taste M) und Tasten (Z/U) greifen beide auf den Tageslauf zu — der Abgleich pro Bild
    // erkennt an diesen Spiegelwerten, welche Seite sich zuletzt geändert hat
    private float _menuTimeOfDay;
    private float _menuTimeFlow;

    private TerrainMode _mode;
    private float _shake;
    private Vector3 _shakeOffset;

    public VoxelWorld World { get; }
    public ChunkMeshManager Meshes { get; }
    public ParticleSystem Particles { get; }
    public PlayerController Player { get; }
    public DayNightCycle DayNight { get; }
    public WorldStorage Storage { get; }

    public Camera3D Camera { get; private set; }

    /// <summary>Sekunden seit dem Start der Szene — treibt Sterne und Wolken</summary>
    public float ElapsedTime { get; private set; }

    /// <summary>Terraforming per Maustaste erlauben</summary>
    public bool AllowEditing { get; set; }

    /// <summary>Chunk-Grenzen und Weltgitter einblenden (F3)</summary>
    public bool ShowDebugGeometry { get; set; }

    /// <summary>Die drei Voxel-Stufen; Umschalten meshed die Welt neu, ändert aber keine Daten</summary>
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

        // Startbereich sofort laden, damit der Spieler auf festem Boden landet
        World.EnsureAround(options.Spawn, options.PreloadRadius);

        DayNight = new DayNightCycle
        {
            Center = options.Spawn,
            DayLengthSeconds = settings.DayLengthSeconds,
            OrbitRadius = 300f,
            DrawSunAndMoon = options.SunAndMoon,
            TimeOfDayHours = settings.TimeOfDay,
            TimeScale = settings.TimeFlow,
        };

        _menuTimeOfDay = settings.TimeOfDay;
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

    /// <summary>Nächste Voxel-Stufe (Blocks → Sculpt → Smooth → Blocks)</summary>
    public void CycleMode() => Mode = (TerrainMode)(((int)Mode + 1) % 3);

    /// <summary>Kamera für kurze Zeit zittern lassen, etwa nach einem Einschlag in der Nähe</summary>
    public void Shake(float strength) => _shake = MathF.Max(_shake, strength);

    public void Update(float dt)
    {
        ElapsedTime += dt;

        Camera = Player.Update(World, dt);

        // Welt um den Spieler streamen (Budget: max. 2 neue Chunks pro Bild)
        World.UpdateStreaming(Player.Position, 2);

        UpdateDayNight(dt);

        World.ShowPreviewAlways = ShowDebugGeometry;
        if (AllowEditing)
        {
            UpdateToolSize();
            World.Update(Camera, Player.Bounds, _settings);
        }

        // Partikel laufen über den unbeleuchteten Default-Shader → Weltlicht beim Spawn einbacken
        Vector3 light = DayNight.AmbientColor + DayNight.SunlightColor * 0.8f;
        Particles.LightScale = Math.Clamp((light.X + light.Y + light.Z) / 3f, 0.15f, 1.1f);
        Particles.Update(World, dt);

        ApplyShake(dt);
    }

    /// <summary>
    /// Fertige Meshes aus den Worker-Threads hochladen. Muss auch bei offenem Menü laufen,
    /// sonst bleibt die Welt nach einem Moduswechsel halb gebaut stehen.
    /// </summary>
    public void PumpMeshUploads() => Meshes.Update();

    /// <summary>Mausrad stellt die Bau-Reichweite, mit Strg den Radius des Kugel-Brushes</summary>
    private void UpdateToolSize()
    {
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel == 0f) return;

        if (Raylib.IsKeyDown(KeyboardKey.LeftControl))
            _settings.SculptRadius = Math.Clamp(_settings.SculptRadius + wheel * 0.1f, 0.25f, 2.5f);
        else
            _settings.BuildReach = Math.Clamp(_settings.BuildReach + wheel, 2f, 60f);

        World.PulsePreview(); // Vorschau kurz zeigen, damit die neue Größe sichtbar wird
    }

    private void UpdateDayNight(float dt)
    {
        // Sonne und Mond wandern mit dem Spieler mit — wirken dadurch unendlich fern
        DayNight.Center = Player.Position;
        DayNight.DayLengthSeconds = _settings.DayLengthSeconds;

        if (_settings.TimeOfDay != _menuTimeOfDay) DayNight.TimeOfDayHours = _settings.TimeOfDay;
        if (_settings.TimeFlow != _menuTimeFlow) DayNight.TimeScale = _settings.TimeFlow;

        DayNight.Update(dt);

        // Nur das Tempo zurückspiegeln — die Uhrzeit im Menü bleibt der gesetzte Sprungpunkt,
        // sonst stünde sie beim Beenden auf der Nachtzeit
        _settings.TimeFlow = DayNight.TimeScale;
        _menuTimeOfDay = _settings.TimeOfDay;
        _menuTimeFlow = _settings.TimeFlow;
    }

    private void ApplyShake(float dt)
    {
        if (_shake <= 0f)
        {
            _shakeOffset = Vector3.Zero;
            return;
        }

        _shake = MathF.Max(0f, _shake - dt * 2.5f);
        _shakeOffset = new Vector3(
            Random.Shared.NextSingle() - 0.5f,
            Random.Shared.NextSingle() - 0.5f,
            Random.Shared.NextSingle() - 0.5f) * _shake * 0.6f;

        Camera3D shaken = Camera;
        shaken.Position += _shakeOffset;
        shaken.Target += _shakeOffset;
        Camera = shaken;
    }

    /// <summary>
    /// Krater schlagen: weiche Flanke, damit die Kuhle auch im Smooth-Modus rund wird,
    /// dazu Trümmer, Feuer und Rauch am Einschlagpunkt.
    /// </summary>
    public void Explode(Vector3 center, float radius, float power = 1.35f)
    {
        Color debris = TerrainColors.ForBlock(
            BlockRegistry.Terrain, (int)center.X, (int)center.Y, (int)center.Z);

        Particles.SpawnExplosion(center, debris, power);

        // Der Spieler darf beim Sprengen nicht als Bauhindernis zählen — leere Box statt Spielerbox
        var noBounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MaxValue));
        World.SculptBlob(center, radius, add: false, edge: radius * 0.36f, BlockRegistry.Stone, noBounds);
    }

    /// <summary>Oberkante des Geländes an dieser XZ-Position (Y des ersten freien Blocks darüber)</summary>
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
        // Himmel als vertikaler Verlauf: Zenit dunkler, Horizont heller (= Fog-Farbe)
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
