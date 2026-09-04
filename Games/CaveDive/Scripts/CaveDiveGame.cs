using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Config;
using VoxelEngine.Core;
using VoxelEngine.Rendering;
using VoxelEngine.Scenes;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Games.CaveDive;

/// <summary>
/// Underground, in the dark, with a lantern. The world is the engine's cave network in Smooth
/// mode only: dig through with the brush, leave glowing lamps behind so the way back stays lit,
/// and chip crystals off the walls. A game built entirely from what the engine offers, and the
/// proof that a game with its own rules needs nothing from the engine's other games.
/// </summary>
[GameDefinition("CaveDive", "Cave Dive", "Underground with a lantern: dig, light the way, collect crystals")]
public sealed class CaveDiveGame : Game
{
    private const int WorldHeight = 256;
    private const float LanternRange = 22f;
    private const float LampRange = 16f;
    private const float CrystalReach = 48f;

    private static readonly Vector3 LanternColor = new(1.0f, 0.86f, 0.62f);
    private static readonly Vector3 LampColor = new(1.0f, 0.7f, 0.35f);

    private static class Materials
    {
        public static readonly byte Crystal = BlockRegistry.Register("Cave crystal", new Color(140, 235, 255, 255), hardness: 0.5f, emissive: 0.9f);
        public static readonly byte Lamp = BlockRegistry.Register("Cave lamp", new Color(255, 190, 110, 255), hardness: 0.5f, emissive: 0.8f);
    }

    private VoxelTerrainScene _scene = null!;
    private CaveGenerator _generator = null!;
    private readonly HashSet<Vector3> _collected = new();
    private float _flicker;
    private float _time;
    private int _lamps;

    public override Camera3D Camera => _scene.Camera;

    public override bool SupportsSaving => true;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: walk and look, Shift sprints, Space jumps",
        "LMB: dig (hold) | RMB: place a lamp, it lights the way back",
        "Wheel: reach | Ctrl+Wheel: brush size",
        "Crystals glow on the cave walls; dig them out to collect them",
    };

    public override void Load()
    {
        _generator = new CaveGenerator(Materials.Crystal, seed: 9001);

        _scene = new VoxelTerrainScene(Context, new VoxelTerrainOptions
        {
            SaveSlot = "cave-dive",
            Spawn = new Vector3(128, 40, 128),
            SpawnOnSurface = false,
            Generator = _generator,
            Mode = TerrainMode.Smooth,
            WorldHeight = WorldHeight,
            BuildMaterial = Materials.Lamp,
            RunDayNight = false,
            SunAngleDegrees = 270f, // midnight: no sun, only what the lantern reaches
            Clouds = false,
            Stars = false,
            SunAndMoon = false,
            MaxViewDistanceChunks = 10,
            Settings = Context.Store.Load("CaveDive/Terrain", () => new TerrainSettings
            {
                FogStart = 14f,
                FogEnd = 80f,
                SculptRadius = 0.9f,
                BuildReach = 6f,
            }),
        });

        Context.Audio.Define("crystal", SfxShape.Pickup);

        _scene.HeadLight = new PointLight(Vector3.Zero, LanternRange, LanternColor);
        _scene.World.BlockPlaced += OnLampPlaced;

        DropIntoACave(_scene.Player.Position);
    }

    /// <summary>The nearest pocket of air below the surface: two blocks of headroom over a floor</summary>
    private void DropIntoACave(Vector3 near)
    {
        int centreX = (int)MathF.Floor(near.X);
        int centreZ = (int)MathF.Floor(near.Z);

        for (int ring = 0; ring < 48; ring += 2)
        for (int dz = -ring; dz <= ring; dz += 2)
        for (int dx = -ring; dx <= ring; dx += 2)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;

            int x = centreX + dx;
            int z = centreZ + dz;
            int surface = (int)_scene.SurfaceHeight(x, z);

            for (int y = surface - 6; y > 4; y--)
            {
                if (BlockRegistry.IsSolid(_scene.World.GetBlock(x, y, z))) continue;
                if (BlockRegistry.IsSolid(_scene.World.GetBlock(x, y + 1, z))) continue;
                if (!BlockRegistry.IsSolid(_scene.World.GetBlock(x, y - 1, z))) continue;

                _scene.Player.Teleport(new Vector3(x + 0.5f, y + 0.05f, z + 0.5f));
                return;
            }
        }

        // No cave within reach: the surface will do, and the player digs in
        _scene.Player.Teleport(new Vector3(near.X, _scene.SurfaceHeight(near.X, near.Z) + 0.5f, near.Z));
    }

    private void OnLampPlaced(Vector3 position, Color albedo)
    {
        // One light per lamp, not one per stamp of a stroke
        if (_scene.HasLightNear(position, 2.5f)) return;

        _scene.AddLight(position, LampRange, LampColor, float.PositiveInfinity);
        _lamps++;
    }

    public override void Update(float dt)
    {
        _time += dt;
        _scene.ShowDebugGeometry = Context.DebugOverlay;

        // The lantern breathes a little, which is what makes it read as a flame
        _flicker = 0.92f + 0.08f * MathF.Sin(_time * 9f) * MathF.Sin(_time * 3.7f + 1f);
        _scene.HeadLight = new PointLight(Vector3.Zero, LanternRange * _flicker, LanternColor);

        _scene.Update(dt);
        CollectCrystals();
    }

    /// <summary>A crystal that is no longer in the world was dug out</summary>
    private void CollectCrystals()
    {
        Vector3 at = _scene.Player.Position;
        int chunkX = (int)MathF.Floor(at.X / Chunk.Size);
        int chunkZ = (int)MathF.Floor(at.Z / Chunk.Size);

        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!_generator.Crystals.TryGetValue(new ChunkCoord(chunkX + dx, chunkZ + dz), out List<Vector3>? crystals)) continue;

            foreach (Vector3 crystal in crystals)
            {
                if (_collected.Contains(crystal)) continue;
                if (Vector3.DistanceSquared(crystal, at) > CrystalReach * CrystalReach) continue;
                if (_scene.World.GetBlock((int)MathF.Floor(crystal.X), (int)MathF.Floor(crystal.Y), (int)MathF.Floor(crystal.Z)) == Materials.Crystal) continue;

                _collected.Add(crystal);
                _scene.AddLight(crystal, 8f, new Vector3(0.5f, 1.2f, 1.4f), 0.6f);
                Context.Audio.Play("crystal", 0.6f, 0.9f + Random.Shared.NextSingle() * 0.3f);
            }
        }
    }

    public override void UpdateAlways() => _scene.PumpMeshUploads();

    public override void DrawBackground() => _scene.DrawBackground();

    public override void DrawWorld() => _scene.Draw();

    public override void DrawHud()
    {
        int width = Context.ScreenWidth;
        int height = Context.ScreenHeight;

        Vector3 at = _scene.Player.Position;
        float depth = _scene.SurfaceHeight(at.X, at.Z) - at.Y;

        Hud.Text($"Crystals {_collected.Count}", 16, 40, 24, new Color(140, 235, 255, 255));
        Hud.Text($"Lamps {_lamps}", 16, 70, 20);
        Hud.Text($"Depth {MathF.Max(0f, depth):0} m", 16, 96, 20, depth > 60f ? Hud.Warning : Hud.Ink);

        Hud.Crosshair(width, height, new Color(255, 220, 160, 255));
        Hud.Centered("LMB dig | RMB lamp | Wheel reach | ESC menu", width / 2, height - 34, 16);

        if (Context.DebugOverlay)
            Hud.Text(
                $"Sections {_scene.Meshes.VisibleSections}/{_scene.Meshes.MeshedChunks} | Loaded {_scene.World.LoadedChunkCount} | " +
                $"Queue {_scene.Meshes.PendingChunks} | Tris {_scene.Meshes.TotalTriangles / 1000}k",
                16, 122, 18, Color.SkyBlue);
    }

    public override bool SaveGame() => _scene.Save();

    public override bool LoadGame() => _scene.Load();

    public override void Unload() => _scene.Dispose();
}
