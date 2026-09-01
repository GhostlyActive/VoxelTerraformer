using Raylib_cs;
using System.Numerics;
using VoxelEngine.Core;
using VoxelEngine.Scenes;
using VoxelEngine.UI;
using VoxelEngine.World;

namespace Terraformer.Games.FreeWalk;

/// <summary>
/// Die Sandbox: eine endlose Voxelwelt ohne Ziel und ohne Gegner. Bauen, graben, zwischen den
/// drei Voxel-Stufen wechseln, an den Reglern drehen. Das Spiel, mit dem das Projekt startet.
/// </summary>
public sealed class FreeWalkGame : Game
{
    private VoxelTerrainScene _scene = null!;

    public override Camera3D Camera => _scene.Camera;

    public override bool SupportsSaving => true;

    public override IReadOnlyList<string> ControlHints => new[]
    {
        "WASD + mouse: walk and look, Shift sprints, Space jumps",
        "LMB / RMB: remove and place (hold them in Sculpt and Smooth)",
        "Wheel: build reach | Ctrl+Wheel: brush size",
        "V: voxel mode | Z/U: day speed | M: tuning | F3: debug",
    };

    public override void Load()
    {
        _scene = new VoxelTerrainScene(Context.Settings, new VoxelTerrainOptions
        {
            // Historischer Ordnername: bestehende Spielstände bleiben so lesbar
            SaveSlot = "world",
            Spawn = new Vector3(128, 60, 128),
            Mode = TerrainMode.Blocks,
        });
    }

    public override void Update(float dt)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.V)) _scene.CycleMode();

        _scene.ShowDebugGeometry = Context.DebugOverlay;
        _scene.Update(dt);
    }

    public override void UpdateWhilePaused() => _scene.PumpMeshUploads();

    public override void DrawBackground() => _scene.DrawBackground();

    public override void DrawWorld() => _scene.Draw();

    public override void DrawHud()
    {
        Hud.Text("WASD move | Shift sprint | Space jump | LMB remove | RMB place | V mode", 10, 40, 20, Color.Black);
        Hud.Text("Wheel: reach | Ctrl+Wheel: brush | Z/U day | F3 debug | M tuning | ESC menu", 10, 65, 20, Color.Black);
        Hud.Text(_scene.DayNight.SpeedLabel, 10, 90, 20, Color.Black);

        if (Context.DebugOverlay)
        {
            Vector3 position = _scene.Player.Position;
            string stats =
                $"Pos {position.X:0} {position.Y:0} {position.Z:0} | " +
                $"Chunks {_scene.Meshes.VisibleChunks}/{_scene.Meshes.MeshedChunks} | " +
                $"Loaded {_scene.World.LoadedChunkCount} | " +
                $"Verts {_scene.Meshes.TotalVertices / 1000}k | " +
                $"Queue {_scene.Meshes.PendingChunks} | " +
                $"Particles {_scene.Particles.ActiveParticles}";
            Hud.Text(stats, 10, 115, 20, Color.DarkBlue);
        }

        Hud.Crosshair(Context.ScreenWidth, Context.ScreenHeight);

        float radius = Context.Settings.SculptRadius;
        float reach = Context.Settings.BuildReach;
        string mode = _scene.Mode switch
        {
            TerrainMode.Blocks => $"Blocks | Reach {reach:0}",
            TerrainMode.Sculpt => $"Sculpt r={radius:0.0} | Reach {reach:0}",
            _ => $"Smooth r={radius:0.0} soft={Context.Settings.BrushSoftness:0.0} | Reach {reach:0}",
        };

        Hud.Centered(mode, Context.ScreenWidth / 2, Context.ScreenHeight - 40, 16);
    }

    public override bool SaveGame() => _scene.Save();

    public override bool LoadGame() => _scene.Load();

    public override void Unload() => _scene.Dispose();
}
