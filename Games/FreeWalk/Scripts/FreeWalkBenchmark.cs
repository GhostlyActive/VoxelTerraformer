using Raylib_cs;
using System.Numerics;
using VoxelEngine.Core;
using VoxelEngine.Scenes;
using VoxelEngine.World;

namespace Games.FreeWalk;

/// <summary>
/// A scripted camera run for <c>--bench</c>: wait for the world to fill, then measure a still view,
/// a fast sideways run through the streaming and level-of-detail borders, a switch to Smooth and
/// the same two views again. Prints one line per phase; the numbers that matter are the worst
/// frame and how many frames missed 60 fps, because a stall is what a player feels.
/// </summary>
internal sealed class FreeWalkBenchmark
{
    private enum Phase { Load, Still, Move, SculptEdit, Switch, SmoothStill, SmoothMove, SmoothEdit, Done }

    private const int EditCount = 24;
    private const int EditSpacingFrames = 6;
    private int _edits;
    private int _editFrames;
    private int _editFramesTotal;
    private int _editFramesMax;
    private bool _editPending;

    private const float Altitude = 34f;
    private const float RunSpeed = 24f;
    private const int StillFrames = 300;
    private const int MoveFrames = 600;
    private const float MaxWaitSeconds = 45f;

    private readonly VoxelTerrainScene _scene;
    private readonly GameContext _context;
    private readonly List<float> _samples = new();

    private Phase _phase = Phase.Load;
    private int _frames;
    private float _phaseTime;
    private int _idleFrames;
    private Vector3 _origin;

    public FreeWalkBenchmark(VoxelTerrainScene scene, GameContext context, Vector3 spawn)
    {
        _scene = scene;
        _context = context;
        _origin = new Vector3(spawn.X, scene.SurfaceHeight(spawn.X, spawn.Z) + Altitude, spawn.Z);

        scene.AllowPlayerControl = false;
        Place(_origin);
    }

    private void Place(Vector3 position)
    {
        _scene.Player.Teleport(position);
        _scene.Player.PointAt(position + new Vector3(400f, -40f, 0f));
    }

    private bool _skipSample;

    public void Update()
    {
        float frame = Raylib.GetFrameTime();

        // The frame that wrote a screenshot is not the engine's to answer for
        if (_skipSample) _skipSample = false;
        else _samples.Add(frame * 1000f);

        _frames++;
        _phaseTime += frame;

        switch (_phase)
        {
            case Phase.Load:
                bool idle = _scene.Meshes.PendingChunks == 0 && _scene.World.PendingLoads == 0;
                _idleFrames = idle ? _idleFrames + 1 : 0;
                if (_idleFrames >= 20 || _phaseTime > MaxWaitSeconds) Next(Phase.Still);
                break;

            case Phase.Still:
                if (_frames >= StillFrames) Next(Phase.Move);
                break;

            case Phase.Move:
                Place(_origin + new Vector3(RunSpeed * _phaseTime, 0f, 0f));
                if (_frames >= MoveFrames) Next(Phase.SculptEdit);
                break;

            case Phase.SculptEdit:
                if (_frames == 1) _scene.Mode = TerrainMode.Sculpt;
                if (EditStep()) Next(Phase.Switch);
                break;

            case Phase.Switch:
                if (_frames == 1) _scene.Mode = TerrainMode.Smooth;
                bool converted = _scene.Meshes.UnconvertedChunks == 0 && _scene.Meshes.InFlightChunks == 0;
                _idleFrames = converted ? _idleFrames + 1 : 0;
                if (_idleFrames >= 20 || _phaseTime > MaxWaitSeconds) Next(Phase.SmoothStill);
                break;

            case Phase.SmoothStill:
                if (_frames >= StillFrames) Next(Phase.SmoothMove);
                break;

            case Phase.SmoothMove:
                Place(_scene.Player.Position + new Vector3(0f, 0f, RunSpeed * frame));
                if (_frames >= MoveFrames) Next(Phase.SmoothEdit);
                break;

            case Phase.SmoothEdit:
                if (EditStep()) Next(Phase.Done);
                break;

            case Phase.Done:
                _context.RequestQuit();
                break;
        }
    }

    /// <summary>
    /// Carves and builds spheres into the ground below the camera and counts the frames until
    /// the mesh queue is empty again: that is how long a stroke takes to reach the screen.
    /// Returns true once all edits are done.
    /// </summary>
    private bool EditStep()
    {
        if (_editPending)
        {
            _editFrames++;
            if (_scene.Meshes.PendingChunks == 0)
            {
                _editPending = false;
                _editFramesTotal += _editFrames;
                _editFramesMax = Math.Max(_editFramesMax, _editFrames);
            }
            return false;
        }

        if (_edits >= EditCount) return true;
        if (_frames % EditSpacingFrames != 0) return false;

        Vector3 at = _scene.Player.Position;
        var point = new Vector3(at.X + 3f + _edits * 1.5f, _scene.SurfaceHeight(at.X + 3f + _edits * 1.5f, at.Z + 2f) - 0.3f, at.Z + 2f);
        var noBounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MaxValue));
        bool add = _edits % 2 == 1;
        float edge = _scene.Mode == TerrainMode.Smooth ? 0.42f : 0f;

        _scene.World.SculptBlob(point + (add ? Vector3.UnitY * 1.5f : Vector3.Zero), 0.9f, add, edge, BlockRegistry.Stone, noBounds);
        _scene.PumpMeshUploads();

        _edits++;
        _editFrames = 0;
        _editPending = true;
        return false;
    }

    private void Next(Phase next)
    {
        Report();

        // The still view is the overview picture of the world; taken after the measurement so the
        // read-back does not land in it
        if (_phase == Phase.Still)
        {
            Raylib.TakeScreenshot("bench-view.png");
            _skipSample = true;
        }

        if (_edits > 0)
        {
            Console.WriteLine($"[bench] {_phase,-11} edits {_edits}: mesh visible after avg {_editFramesTotal / (float)_edits:F1} frames, max {_editFramesMax} frames");
            _edits = 0;
            _editFramesTotal = 0;
            _editFramesMax = 0;
            _editPending = false;
        }

        _phase = next;
        _frames = 0;
        _phaseTime = 0f;
        _idleFrames = 0;
        _samples.Clear();

        if (next == Phase.Done) _context.RequestQuit();
    }

    private void Report()
    {
        if (_samples.Count == 0) return;

        float[] sorted = _samples.ToArray();
        Array.Sort(sorted);

        float sum = 0f;
        int over = 0;
        foreach (float ms in sorted)
        {
            sum += ms;
            if (ms > 16.7f) over++;
        }

        float p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99f))];

        Console.WriteLine(
            $"[bench] {_phase,-11} {_phaseTime,6:F2}s {sorted.Length,5} frames | avg {sum / sorted.Length:F2} ms | p99 {p99:F2} ms | " +
            $"max {sorted[^1]:F2} ms | over 16.7 ms: {over} | sections {_scene.Meshes.VisibleSections} | draws {_scene.Meshes.DrawCalls} | " +
            $"tris {_scene.Meshes.TotalTriangles / 1000}k | gpu {_scene.Meshes.GpuBytes / (1024 * 1024)} MB | " +
            $"loaded {_scene.World.LoadedChunkCount} | meshed {_scene.Meshes.MeshedChunks} | unconverted {_scene.Meshes.UnconvertedChunks} | dirty {_scene.Meshes.PendingChunks} | " +
            $"wave {(_scene.Transition == null ? "done" : _scene.Transition.DisplayRadius.ToString("0") + " m")} | rss {Environment.WorkingSet / (1024 * 1024)} MB");
    }
}
