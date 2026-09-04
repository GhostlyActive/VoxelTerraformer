using Raylib_cs;
using System.Numerics;
using VoxelEngine.Core;
using VoxelEngine.Scenes;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Games.FreeWalk;

/// <summary>
/// The sandbox: an endless voxel world with no goal and no enemies. Build, dig, switch between
/// the three voxel modes, turn the dials, and get up the mountains with the jetpack. The game
/// the project opens with.
/// </summary>
[GameDefinition("FreeWalk", "Free Walk", "Sandbox: build, dig and switch between the voxel modes")]
public sealed class FreeWalkGame : Game
{
    /// <summary>Sun pinned at its highest point: even light, and no night to sit through</summary>
    private const float NoonAngle = 90f;

    private VoxelTerrainScene _scene = null!;
    private FreeWalkBenchmark? _benchmark;

    public override Camera3D Camera => _scene.Camera;

    public override bool SupportsSaving => true;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: walk and look, Shift sprints, Space jumps",
        "Hold Space in the air: jetpack. The tank refills on the ground",
        "LMB / RMB: remove and place (hold them in Sculpt and Smooth)",
        "Wheel: build reach | Ctrl+Wheel: brush size",
        "V: voxel mode | Z/U: move the sun | M: tuning | F3: debug",
    };

    public override void Load()
    {
        _scene = new VoxelTerrainScene(Context, new VoxelTerrainOptions
        {
            // Historic folder name, so existing saves stay readable
            SaveSlot = "world",
            Spawn = new Vector3(128, 60, 128),
            Mode = TerrainMode.Blocks,
            RunDayNight = false,
            SunAngleDegrees = NoonAngle,
            Jetpack = true,
        });

        if (Context.Benchmark) _benchmark = new FreeWalkBenchmark(_scene, Context, new Vector3(128, 60, 128));
    }

    public override void Update(float dt)
    {
        if (_benchmark != null) _benchmark.Update();
        else if (Raylib.IsKeyPressed(KeyboardKey.V)) _scene.CycleMode();

        _scene.ShowDebugGeometry = Context.DebugOverlay && _benchmark == null;
        _scene.Update(dt);
    }

    public override void UpdateAlways() => _scene.PumpMeshUploads();

    public override void DrawBackground() => _scene.DrawBackground();

    public override void DrawWorld() => _scene.Draw();

    public override void DrawHud()
    {
        Hud.Text("WASD move | Shift sprint | Space jump, hold for jetpack | LMB remove | RMB place | V mode", 10, 40, 20, Color.Black);
        Hud.Text("Wheel: reach | Ctrl+Wheel: brush | Z/U sun | F3 debug | M tuning | ESC menu", 10, 65, 20, Color.Black);
        Hud.Text(_scene.DayNight.SunLabel, 10, 90, 20, Color.Black);

        if (Context.DebugOverlay)
        {
            Vector3 position = _scene.Player.Position;
            string stats =
                $"Pos {position.X:0} {position.Y:0} {position.Z:0} | " +
                $"Sections {_scene.Meshes.VisibleSections}/{_scene.Meshes.MeshedChunks} chunks | " +
                $"Loaded {_scene.World.LoadedChunkCount}+{_scene.World.PendingLoads} | " +
                $"Tris {_scene.Meshes.TotalTriangles / 1000}k | GPU {_scene.Meshes.GpuBytes / (1024 * 1024)} MB | " +
                $"Draws {_scene.Meshes.DrawCalls} | Queue {_scene.Meshes.PendingChunks} | " +
                $"Workers {_scene.Meshes.WorkerCount} | Particles {_scene.Particles.ActiveParticles}";
            Hud.Text(stats, 10, 115, 20, Color.DarkBlue);
        }

        Hud.Crosshair(Context.ScreenWidth, Context.ScreenHeight);
        DrawFuel();

        float radius = _scene.Settings.SculptRadius;
        float reach = _scene.Settings.BuildReach;
        string tool = _scene.Mode switch
        {
            TerrainMode.Blocks => $"Reach {reach:0}",
            TerrainMode.Sculpt => $"Brush r={radius:0.0} | Reach {reach:0}",
            _ => $"Brush r={radius:0.0} soft={_scene.Settings.BrushSoftness:0.0} | Reach {reach:0}",
        };

        ModeTransition? wave = _scene.Transition;
        ModeIndicator.Draw(Context.ScreenWidth / 2, Context.ScreenHeight - 74, _scene.Mode, _scene.SinceModeSwitch,
            wave?.Progress01(_scene.FogEnd) ?? 1f, wave != null && wave.Remeshes, "V");
        Hud.Centered(tool, Context.ScreenWidth / 2, Context.ScreenHeight - 34, 16);
    }

    /// <summary>The jetpack's tank, bottom left; it flashes when the jets are on</summary>
    private void DrawFuel()
    {
        float fuel = _scene.Player.Fuel01;
        bool burning = _scene.Player.JetpackBurning;

        Color color = fuel > 0.3f ? Hud.Accent : Hud.Warning;
        if (burning) color = new Color(255, 230, 150, 255);

        Hud.Bar(16, Context.ScreenHeight - 60, 180, 16, fuel, color, burning ? "JETPACK" : $"jetpack {fuel * 100f:0}%");
    }

    public override string DebugReport()
    {
        Vector3 position = _scene.Player.Position;
        var coord = new ChunkCoord((int)MathF.Floor(position.X / Chunk.Size), (int)MathF.Floor(position.Z / Chunk.Size));

        var report = new System.Text.StringBuilder();
        report.AppendLine($"[report] player {position} chunk {coord} surface {_scene.SurfaceHeight(position.X, position.Z)} elapsed {_scene.ElapsedTime:F2}s grounded {_scene.Player.IsGrounded}");

        var solidAbove = new System.Text.StringBuilder();
        for (int x = (int)MathF.Floor(position.X - 0.3f); x <= (int)MathF.Floor(position.X + 0.3f); x++)
        for (int z = (int)MathF.Floor(position.Z - 0.3f); z <= (int)MathF.Floor(position.Z + 0.3f); z++)
        for (int y = 12; y < _scene.World.Height; y++)
            if (BlockRegistry.IsSolid(_scene.World.GetBlock(x, y, z))) solidAbove.Append($" ({x},{y},{z})");
        report.AppendLine($"[report] solid blocks above y=12 under the player:{solidAbove}");
        report.AppendLine($"[report] loaded {_scene.World.LoadedChunkCount} pending {_scene.World.PendingLoads} meshed {_scene.Meshes.MeshedChunks} queue {_scene.Meshes.PendingChunks} tris {_scene.Meshes.TotalTriangles} gpu {_scene.Meshes.GpuBytes / (1024 * 1024)} MB draws {_scene.Meshes.DrawCalls}");
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            report.AppendLine("[report] " + _scene.Meshes.Describe(new ChunkCoord(coord.X + dx, coord.Z + dz)));

        return report.ToString();
    }

    public override bool SaveGame() => _scene.Save();

    public override bool LoadGame() => _scene.Load();

    public override void Unload() => _scene.Dispose();
}
