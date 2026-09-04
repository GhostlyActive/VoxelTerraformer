using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.MathTools;
using VoxelEngine.Rendering;

namespace VoxelEngine.World;

public class VoxelWorld : IDisposable
{
    public const int WorldHeight = 64;

    /// <summary>Chunks the streaming can be asked to keep loaded around the player, at most</summary>
    public const int MaxViewDistance = 48;

    // Streaming: chunks are requested in a circle around the player, built on worker threads
    // and adopted a few per frame; they are dropped with hysteresis two chunks further out
    private int _loadRadius;
    private (int X, int Z)[] _loadOrder = Array.Empty<(int, int)>();

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();
    private readonly WorldStorage? _storage;
    private readonly ITerrainGenerator _generator;
    private readonly ChunkLoader _loader;
    private readonly HashSet<ChunkCoord> _pendingLoads = new();

    // Bumped by "Load world"; results built for an older world are dropped when they arrive
    private int _worldGeneration;

    private const int AdoptBudgetPerFrame = 8;
    private const int UnloadBudgetPerFrame = 24;
    private const int MaxPendingLoads = 12;

    // Changed chunks survive unloading in memory; only what the player explicitly saves goes to
    // disk. On startup the world is always fresh.
    private readonly Dictionary<ChunkCoord, Chunk> _keptModified = new();
    private bool _diskIsBase; // only after a save or load is the file the basis for loading chunks

    // What the crosshair points at: either a block that was hit (hover) or, with nothing in reach,
    // an empty cell in mid-air (ghost)
    private bool _hasHover;
    private VoxelRaycast.Vector3Int _hoverBlock;
    private VoxelRaycast.Vector3Int _hoverPlaceCell;
    private Vector3 _hoverCenter;

    private bool _hasGhost;
    private VoxelRaycast.Vector3Int _ghostCell;
    private Vector3 _ghostCenter;

    // Sculpt mode: a sphere brush on the sub-voxel density field; holding it draws a continuous stroke
    private bool _hasSculptTarget;
    private float _sculptRadiusForDraw;
    private EngineSettings _settings = new();

    // A stroke is anchored to the surface it started on: while the button is held the brush
    // follows the crosshair across the plane through that point, not across whatever the ray hits
    // now. Otherwise adding material would hit the material just added and race towards the
    // camera, and carving through a wall would jump to whatever lies behind it.
    private bool _stroking;
    private bool _strokeAdding;
    private bool _brushActive;
    private Vector3 _anchorPoint;
    private Vector3 _anchorNormal;
    private Vector3 _strokePrevious;
    private float _strokeFeedbackCooldown;

    // Where the brush bites this frame, for the preview and the feedback
    private Vector3 _previewCenter;

    // The brush sphere and the block outline get in the way while building, so they show briefly
    // after a size change (wheel), at low strength all the time when ShowBrushAlways is set, and
    // at full strength while the debug overlay is on
    private float _previewTimer;

    private const float StrokeStepFactor = 0.5f;    // stamp spacing as a fraction of the radius
    private const int StrokeMaxStamps = 64;         // work budget for one frame of a fast stroke
    private const float StrokeMinMove = 0.05f;      // radii the target has to move before it is stamped again
    private const float ReanchorSlack = 1.0f;       // radii the true surface may leave the plane before the stroke follows it
    private const float FeedbackInterval = 0.07f;   // seconds between particle bursts and sounds along a stroke

    /// <summary>Switched with key V; see <see cref="TerrainMode"/></summary>
    public TerrainMode Mode { get; set; } = TerrainMode.Blocks;

    /// <summary>What the player places and sculpts with; a game sets its own material here</summary>
    public byte BuildMaterial { get; set; } = BlockRegistry.Stone;

    /// <summary>Both fine modes use the same sphere brush; only the presentation differs</summary>
    public bool UsesSculptTool => Mode != TerrainMode.Blocks;

    /// <summary>
    /// Chunk needs a new mesh between the two block heights, inclusive, and why. Also fires for
    /// neighbours when an edit touches an edge, because face culling and ambient occlusion cross
    /// chunk borders.
    /// </summary>
    public event Action<ChunkCoord, int, int, MeshReason>? ChunkDirty;

    /// <summary>Block removed: centre plus albedo, for particles among others</summary>
    public event Action<Vector3, Color>? BlockBroken;

    /// <summary>Block placed: centre plus albedo</summary>
    public event Action<Vector3, Color>? BlockPlaced;

    /// <summary>Chunk was unloaded, so its mesh can go</summary>
    public event Action<ChunkCoord>? ChunkUnloaded;

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public int LoadedChunkCount => _chunks.Count;

    /// <summary>Chunks requested from the workers and not yet adopted</summary>
    public int PendingLoads => _pendingLoads.Count;

    /// <summary>Radius in chunks that streaming keeps loaded around the player</summary>
    public int LoadRadius => _loadRadius;

    public int UnloadRadius => _loadRadius + 2;

    /// <param name="storage">Where saves go; null for a world that cannot be saved or loaded</param>
    public VoxelWorld(WorldStorage? storage, ITerrainGenerator? generator = null, int viewDistanceChunks = 24)
    {
        _storage = storage;
        _generator = generator ?? new DefaultTerrainGenerator();

        // Generation is cheap next to meshing, so two threads keep up with any walking speed
        _loader = new ChunkLoader(_storage, _generator, WorldHeight, Math.Clamp(Environment.ProcessorCount / 4, 1, 3));

        SetViewDistance(viewDistanceChunks);
    }

    /// <summary>Change how far the world streams; takes effect over the next frames</summary>
    public void SetViewDistance(int chunks)
    {
        chunks = Math.Clamp(chunks, 2, MaxViewDistance);
        if (chunks == _loadRadius) return;

        _loadRadius = chunks;
        _loadOrder = BuildLoadOrder(chunks);
    }

    public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        => _chunks.TryGetValue(coord, out chunk!);

    /// <summary>Loaded with all eight neighbours present, so a mesh built now stays valid</summary>
    public bool IsMeshable(ChunkCoord coord)
        => _chunks.TryGetValue(coord, out Chunk? chunk) && chunk.Surrounded;

    /// <summary>The 3x3 neighbourhood as [dx + 1 + 3 * (dz + 1)]; missing chunks are null</summary>
    public void GetNeighbourhood(ChunkCoord coord, Chunk?[] buffer)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            buffer[dx + 1 + 3 * (dz + 1)] = _chunks.TryGetValue(new ChunkCoord(coord.X + dx, coord.Z + dz), out Chunk? chunk) ? chunk : null;
    }

    // --- Streaming ---

    /// <summary>
    /// Loads everything within the radius right away (blocking), for the spawn area at startup.
    /// The chunks are built in parallel and adopted in one go.
    /// </summary>
    public void EnsureAround(Vector3 position, int radiusChunks)
    {
        (int pcx, int pcz) = PositionToChunk(position);

        var missing = new List<ChunkCoord>();
        for (int dz = -radiusChunks; dz <= radiusChunks; dz++)
        for (int dx = -radiusChunks; dx <= radiusChunks; dx++)
        {
            var coord = new ChunkCoord(pcx + dx, pcz + dz);
            if (_chunks.ContainsKey(coord)) continue;

            if (_keptModified.Remove(coord, out Chunk? kept))
            {
                AddChunk(coord, kept);
                continue;
            }

            missing.Add(coord);
        }

        var built = new Chunk[missing.Count];
        bool fromDisk = _diskIsBase;
        Parallel.For(0, missing.Count, i => built[i] = Build(missing[i], fromDisk));

        for (int i = 0; i < missing.Count; i++)
            AddChunk(missing[i], built[i]);
    }

    private Chunk Build(ChunkCoord coord, bool fromDisk)
    {
        if (fromDisk && _storage != null && _storage.TryLoad(coord, Chunk.Size * WorldHeight * Chunk.Size, out byte[]? blocks, out Dictionary<int, byte[]>? refinements))
            return new Chunk(coord, blocks!, refinements!);

        return new Chunk(coord, WorldHeight, _generator);
    }

    /// <summary>
    /// Call once per frame: adopts chunks the workers finished, drops distant ones and asks for
    /// the next missing ones nearest first. Everything here is bounded per frame.
    /// </summary>
    public void UpdateStreaming(Vector3 playerPosition)
    {
        (int pcx, int pcz) = PositionToChunk(playerPosition);

        AdoptBuiltChunks(pcx, pcz);
        UnloadDistant(pcx, pcz);
        RequestMissing(pcx, pcz);
    }

    private void AdoptBuiltChunks(int pcx, int pcz)
    {
        int adopted = 0;

        while (adopted < AdoptBudgetPerFrame && _loader.TryTake(out ChunkLoader.Result result))
        {
            _pendingLoads.Remove(result.Coord);

            if (result.WorldGeneration != _worldGeneration) continue; // from before "Load world"
            if (_chunks.ContainsKey(result.Coord)) continue;           // preloaded meanwhile
            if (!Within(result.Coord, pcx, pcz, UnloadRadius)) continue; // the player walked away

            AddChunk(result.Coord, result.Chunk);
            adopted++;
        }
    }

    private void UnloadDistant(int pcx, int pcz)
    {
        List<ChunkCoord>? toUnload = null;
        foreach (ChunkCoord coord in _chunks.Keys)
        {
            if (Within(coord, pcx, pcz, UnloadRadius)) continue;

            (toUnload ??= new List<ChunkCoord>()).Add(coord);
            if (toUnload.Count >= UnloadBudgetPerFrame) break;
        }

        if (toUnload == null) return;

        foreach (ChunkCoord coord in toUnload)
            UnloadChunk(coord);
    }

    private void RequestMissing(int pcx, int pcz)
    {
        foreach ((int offsetX, int offsetZ) in _loadOrder)
        {
            if (_pendingLoads.Count >= MaxPendingLoads) break;

            var coord = new ChunkCoord(pcx + offsetX, pcz + offsetZ);
            if (_chunks.ContainsKey(coord) || _pendingLoads.Contains(coord)) continue;

            // A chunk with unsaved edits never goes back through the generator
            if (_keptModified.Remove(coord, out Chunk? kept))
            {
                AddChunk(coord, kept);
                continue;
            }

            _pendingLoads.Add(coord);
            _loader.Request(coord, _diskIsBase, _worldGeneration);
        }
    }

    private static bool Within(ChunkCoord coord, int pcx, int pcz, int radius)
    {
        int dx = coord.X - pcx;
        int dz = coord.Z - pcz;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>Manual save: the entire current state of the world becomes the save</summary>
    public bool SaveWorld()
    {
        if (_storage == null) return false;

        // Fresh session: the old save is replaced, not mixed into
        if (!_diskIsBase) _storage.DeleteAll();

        bool allWritten = true;

        foreach ((ChunkCoord coord, Chunk chunk) in _chunks)
        {
            if (!chunk.Modified) continue;
            allWritten &= _storage.Save(coord, chunk.RawBlocks, chunk.Refinements);
        }

        foreach ((ChunkCoord coord, Chunk chunk) in _keptModified)
            allWritten &= _storage.Save(coord, chunk.RawBlocks, chunk.Refinements);

        // On failure leave the state untouched; the next attempt writes everything again
        if (!allWritten) return false;

        foreach ((ChunkCoord _, Chunk chunk) in _chunks)
            if (chunk.Modified) chunk.MarkSaved();
        _keptModified.Clear();

        _diskIsBase = true;
        return true;
    }

    /// <summary>
    /// Manual load: discards the current state and restores the save. Only chunks that were
    /// edited or have a file on disk are replaced; generated terrain regenerates identically, so
    /// throwing it away would only cost a reload of the whole view.
    /// </summary>
    public bool LoadWorld()
    {
        // Reject old or foreign format versions, or a fresh world would quietly be loaded instead
        if (_storage == null || !_storage.HasCompatibleSave) return false;

        _keptModified.Clear();
        _diskIsBase = true;
        _worldGeneration++;

        foreach (ChunkCoord coord in _chunks.Keys.ToList())
        {
            Chunk chunk = _chunks[coord];
            if (!chunk.Modified && !_storage.HasChunkFile(coord)) continue;

            _chunks.Remove(coord);
            chunk.Surrounded = false;
            chunk.MeshRequested = false;
            MarkNeighboursUnsurrounded(coord);
            ChunkUnloaded?.Invoke(coord);
        }

        return true;
    }

    private void AddChunk(ChunkCoord coord, Chunk chunk)
    {
        _chunks.Add(coord, chunk);

        // The new chunk may complete the neighbourhood of any of the nine chunks around it.
        // Each one is meshed exactly once, the moment that happens; edits keep it current after.
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            var candidate = new ChunkCoord(coord.X + dx, coord.Z + dz);
            if (!_chunks.TryGetValue(candidate, out Chunk? other) || other.Surrounded) continue;
            if (!HasAllNeighbours(candidate)) continue;

            other.Surrounded = true;
            if (other.MeshRequested) continue;

            other.MeshRequested = true;
            ChunkDirty?.Invoke(candidate, 0, WorldHeight - 1, MeshReason.Stream);
        }
    }

    private bool HasAllNeighbours(ChunkCoord coord)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dz == 0) continue;
            if (!_chunks.ContainsKey(new ChunkCoord(coord.X + dx, coord.Z + dz))) return false;
        }

        return true;
    }

    private void UnloadChunk(ChunkCoord coord)
    {
        if (!_chunks.Remove(coord, out Chunk? chunk)) return;

        chunk.Surrounded = false;
        chunk.MeshRequested = false;
        MarkNeighboursUnsurrounded(coord);

        if (chunk.Modified) _keptModified[coord] = chunk;
        ChunkUnloaded?.Invoke(coord);
    }

    // A neighbour that loses a chunk keeps its mesh (the stale shell sits at the far ring, out
    // of sight) but must not be re-meshed until the gap is filled again
    private void MarkNeighboursUnsurrounded(ChunkCoord coord)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dz == 0) continue;
            if (_chunks.TryGetValue(new ChunkCoord(coord.X + dx, coord.Z + dz), out Chunk? other))
                other.Surrounded = false;
        }
    }

    private static (int cx, int cz) PositionToChunk(Vector3 position)
        => (FloorDiv((int)MathF.Floor(position.X), Chunk.Size),
            FloorDiv((int)MathF.Floor(position.Z), Chunk.Size));

    private static (int X, int Z)[] BuildLoadOrder(int radius)
    {
        var offsets = new List<(int X, int Z)>();
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
            if (dx * dx + dz * dz <= radius * radius)
                offsets.Add((dx, dz));

        offsets.Sort((a, b) => (a.X * a.X + a.Z * a.Z).CompareTo(b.X * b.X + b.Z * b.Z));
        return offsets.ToArray();
    }

    public void Dispose() => _loader.Dispose();

    public void Update(Camera3D camera, BoundingBox playerBounds, EngineSettings settings)
    {
        _settings = settings;
        _previewTimer = Math.Max(0f, _previewTimer - Raylib.GetFrameTime());

        if (UsesSculptTool)
        {
            _hasHover = false;
            _hasGhost = false;
            UpdateSculpt(camera, playerBounds, settings.BuildReach, settings.SculptRadius);
            return;
        }

        _hasSculptTarget = false;
        _stroking = false;
        _brushActive = false;

        // Inputs: Mouse + keyboard fallback
        bool remove = Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.O);
        bool place  = Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.P);

        // Refresh hover and ghost every frame, so removing and placing act exactly on the marked target
        UpdateHover(camera, settings.BuildReach);

        if (remove) TryRemove();
        if (place)  TryPlace(playerBounds);
    }

    /// <summary>Permanent preview (block outline or brush sphere); tied to the debug overlay (F3)</summary>
    public bool ShowPreviewAlways { get; set; }

    /// <summary>Flash the preview briefly, for instance after the reach or brush size changed</summary>
    public void PulsePreview() => _previewTimer = Math.Max(_previewTimer, _settings.PreviewHold);

    /// <summary>Ends a running stroke; the next frame with a button held starts a fresh one. For mode switches.</summary>
    public void CancelStroke() => _stroking = false;

    public void DrawHover()
    {
        // Fade out at the end instead of cutting: a jump is more noticeable than the preview itself
        const float fadeSeconds = 0.35f;
        float visibility = ShowPreviewAlways ? 1f : Math.Clamp(_previewTimer / fadeSeconds, 0f, 1f);

        // The brush stays visible at rest, or the player carves and builds blind: without the
        // sphere there is no telling the radius, the bite point or that a stroke is running
        if (UsesSculptTool && _settings.ShowBrushAlways)
            visibility = MathF.Max(visibility, _brushActive ? 1f : 0.55f);

        if (visibility <= 0f) return;

        if (_hasSculptTarget)
        {
            DrawBrushPreview(visibility);
        }
        else if (_hasHover)
        {
            Raylib.DrawCubeWires(_hoverCenter, 1.02f, 1.02f, 1.02f, Fade(Color.Yellow, visibility));
        }
        else if (_hasGhost)
        {
            Raylib.DrawCubeWires(_ghostCenter, 1f, 1f, 1f, Fade(new Color(95, 225, 235, 220), visibility));
        }
    }

    private static Color Fade(Color color, float factor)
        => new(color.R, color.G, color.B, (byte)(color.A * Math.Clamp(factor, 0f, 1f)));

    /// <summary>
    /// Brush preview: a translucent sphere plus wireframe, green while adding and red while
    /// carving. The pulse makes it visible that a stroke is running.
    /// </summary>
    private void DrawBrushPreview(float visibility)
    {
        bool carving = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool building = Raylib.IsMouseButtonDown(MouseButton.Right);
        bool active = carving || building;

        Color tint = !active
            ? new Color(150, 220, 255, 255)
            : carving ? new Color(255, 120, 90, 255) : new Color(130, 255, 150, 255);

        float pulse = active ? 1f + 0.06f * MathF.Sin((float)Raylib.GetTime() * 14f) : 1f;
        float radius = _sculptRadiusForDraw * pulse;

        Raylib.BeginBlendMode(BlendMode.Alpha);
        Raylib.DrawSphere(_previewCenter, radius, Fade(tint, (active ? 60 : 30) / 255f * visibility));
        Raylib.EndBlendMode();

        Raylib.DrawSphereWires(_previewCenter, radius, 12, 12, Fade(tint, (active ? 200 : 140) / 255f * visibility));
    }

    // --- Sculpt mode ---

    private void UpdateSculpt(Camera3D camera, BoundingBox playerBounds, float buildReach, float sculptRadius)
    {
        _sculptRadiusForDraw = sculptRadius;
        _hasSculptTarget = true;

        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera);
        Vector3 direction = Vector3.Normalize(ray.Direction);

        // Raycast at sub-voxel resolution (coordinates x8), so it also reads drilled holes correctly
        var hit = VoxelRaycast.Cast(
            GetSubVoxel,
            ray.Position * SubVoxels.Divisions,
            direction,
            buildReach * SubVoxels.Divisions);

        bool hasSurface = hit.HasHit;
        float surfaceDistance = hasSurface ? hit.Distance / SubVoxels.Divisions : buildReach;

        // The hit cell's centre, or mid-air at the end of the reach when nothing is in the way
        Vector3 surfacePoint = hasSurface
            ? new Vector3(
                (hit.Block.X + 0.5f) * SubVoxels.CellSize,
                (hit.Block.Y + 0.5f) * SubVoxels.CellSize,
                (hit.Block.Z + 0.5f) * SubVoxels.CellSize)
            : ray.Position + direction * buildReach;

        Vector3 surfaceNormal = hasSurface && hit.Normal.LengthSquared() > 0.5f ? hit.Normal : -direction;

        bool carve = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool build = Raylib.IsMouseButtonDown(MouseButton.Right);
        _brushActive = carve || build;

        if (!_brushActive)
        {
            _stroking = false;
            _previewCenter = surfacePoint;
            return;
        }

        bool add = !carve && build; // with both buttons down, carving wins
        if (_stroking && add != _strokeAdding) _stroking = false;

        bool fresh = !_stroking;
        if (fresh) Anchor(surfacePoint, surfaceNormal);

        Vector3 target = PointOnAnchorPlane(ray.Position, direction, buildReach);
        float planeDistance = Vector3.Distance(ray.Position, target);

        // The stroke follows the true surface again once that has left the plane by more than a
        // radius: carving broke through, or the ray slid off an edge onto something else. A nearer
        // hit is ignored while adding (that is the material just built) but followed while
        // carving, so the bite never floats behind a wall the ray runs into.
        float slack = sculptRadius * ReanchorSlack;
        bool surfaceFellAway = hasSurface ? surfaceDistance > planeDistance + slack : planeDistance > buildReach;
        bool surfaceCameCloser = !add && hasSurface && surfaceDistance < planeDistance - slack;

        if (!fresh && (surfaceFellAway || surfaceCameCloser))
        {
            Anchor(surfacePoint, surfaceNormal);
            target = surfacePoint;
            fresh = true;
        }

        _strokeAdding = add;
        _strokeFeedbackCooldown -= Raylib.GetFrameTime();
        _previewCenter = target;

        // Hard-edged Sculpt mode switches cells outright, or the cube look frays; Smooth lays down
        // a soft falloff, which is what marching cubes turns into a rounded surface
        float edge = Mode == TerrainMode.Smooth ? sculptRadius * _settings.BrushSoftness : 0f;

        bool changed = StampStroke(target, sculptRadius, add, edge, playerBounds, fresh);
        _stroking = true;

        if (changed && _strokeFeedbackCooldown <= 0f)
        {
            _strokeFeedbackCooldown = FeedbackInterval;

            // Debris in the colour of what is actually there: the block under the bite when
            // carving, the material being laid down when building
            int bx = (int)MathF.Floor(target.X), by = (int)MathF.Floor(target.Y), bz = (int)MathF.Floor(target.Z);
            int id = add ? BuildMaterial : GetBlock(bx, by, bz);
            if (!BlockRegistry.IsSolid(id)) id = BlockRegistry.Terrain;
            Color albedo = TerrainColors.ForBlock(id, bx, by, bz);

            if (add) BlockPlaced?.Invoke(target, albedo);
            else BlockBroken?.Invoke(target, albedo);
        }
    }

    private void Anchor(Vector3 point, Vector3 normal)
    {
        _anchorPoint = point;
        _anchorNormal = normal;
        _strokePrevious = point;
    }

    /// <summary>Where the crosshair ray crosses the plane of the current stroke</summary>
    private Vector3 PointOnAnchorPlane(Vector3 origin, Vector3 direction, float reach)
    {
        float facing = Vector3.Dot(direction, _anchorNormal);
        float anchorDistance = Vector3.Distance(origin, _anchorPoint);

        // Looking along the plane: keep the depth of the anchor instead of shooting off to the horizon
        if (MathF.Abs(facing) < 0.2f) return origin + direction * anchorDistance;

        float t = Vector3.Dot(_anchorPoint - origin, _anchorNormal) / facing;
        t = Math.Clamp(t, 0.5f, reach * 1.5f);

        return origin + direction * t;
    }

    /// <summary>
    /// Stamps the brush along the stretch covered since the last frame. A single stamp per frame
    /// would leave a string of beads when the mouse moves fast; overlapping spheres give a
    /// continuous tube instead. <paramref name="fresh"/> marks the first stamp of a stroke, which
    /// has no stretch to fill.
    /// </summary>
    private bool StampStroke(Vector3 target, float radius, bool add, float edge, BoundingBox playerBounds, bool fresh)
    {
        Vector3 delta = target - _strokePrevious;
        float distance = delta.Length();

        // Holding still: the brush would only re-stamp what it already shaped
        if (!fresh && distance < radius * StrokeMinMove) return false;

        bool changed = SculptBlob(target, radius, add, edge, BuildMaterial, playerBounds);

        float step = radius * StrokeStepFactor;
        if (!fresh && distance >= step)
        {
            // Past the budget the stamps spread out instead of the segment being dropped: a
            // slightly coarser line still beats a hole in the stroke
            int stamps = Math.Min((int)(distance / step), StrokeMaxStamps);

            for (int i = 1; i <= stamps; i++)
            {
                Vector3 point = _strokePrevious + delta * (i / (float)(stamps + 1));
                changed |= SculptBlob(point, radius, add, edge, BuildMaterial, playerBounds);
            }
        }

        _strokePrevious = target;
        return changed;
    }

    /// <summary>
    /// A soft sphere brush on the sub-voxel density field: 255 at the core, exactly the iso value
    /// at the radius, running out to 0 over <paramref name="edge"/> metres.
    /// Adding takes the union (maximum), carving subtracts (minimum), so several strokes grow
    /// together into one rounded shape instead of overwriting each other, and stamping the same
    /// spot twice changes nothing.
    /// </summary>
    public bool SculptBlob(Vector3 center, float radius, bool add, float edge, byte blockId, BoundingBox playerBounds)
    {
        // With a falloff over 2*radius the core never reaches full density and the brush hits nothing
        edge = Math.Clamp(edge, 0f, radius * 1.6f);
        float reach = radius + edge;

        int minBlockX = (int)MathF.Floor(center.X - reach);
        int maxBlockX = (int)MathF.Floor(center.X + reach);
        int minBlockY = Math.Max(0, (int)MathF.Floor(center.Y - reach));
        int maxBlockY = Math.Min(WorldHeight - 1, (int)MathF.Floor(center.Y + reach));
        int minBlockZ = (int)MathF.Floor(center.Z - reach);
        int maxBlockZ = (int)MathF.Floor(center.Z + reach);

        bool anyChange = false;

        for (int by = minBlockY; by <= maxBlockY; by++)
        for (int bz = minBlockZ; bz <= maxBlockZ; bz++)
        for (int bx = minBlockX; bx <= maxBlockX; bx++)
        {
            if (BlockDistanceSquared(bx, by, bz, center) > reach * reach) continue; // box corner, outside the sphere

            var (cc, lx, lz) = WorldToChunk(bx, bz);
            if (!_chunks.TryGetValue(cc, out var chunk)) continue;

            int id = chunk.GetLocal(lx, by, lz, WorldHeight);
            bool solid = BlockRegistry.IsSolid(id);
            if (!add && !solid) continue; // air cannot be hollowed out any further

            // Copy-on-write: never change stored fields in place, worker threads read them
            bool refined = chunk.TryGetRefinement(lx, by, lz, WorldHeight, out byte[] existing);
            byte[] working = solid
                ? (refined ? (byte[])existing.Clone() : SubVoxels.NewFull())
                : SubVoxels.NewEmpty();

            bool changed = false;
            bool crossesIso = false;

            // Walk only the cells the brush can actually reach: with a large radius most of the
            // 512 cells per block would otherwise sit in the loop for nothing
            (int minCellX, int maxCellX) = CellRange(center.X, reach, bx);
            (int minCellY, int maxCellY) = CellRange(center.Y, reach, by);
            (int minCellZ, int maxCellZ) = CellRange(center.Z, reach, bz);

            for (int sz = minCellZ; sz <= maxCellZ; sz++)
            for (int sy = minCellY; sy <= maxCellY; sy++)
            for (int sx = minCellX; sx <= maxCellX; sx++)
            {
                var cellCenter = new Vector3(
                    bx + (sx + 0.5f) * SubVoxels.CellSize,
                    by + (sy + 0.5f) * SubVoxels.CellSize,
                    bz + (sz + 0.5f) * SubVoxels.CellSize);

                float distanceSquared = Vector3.DistanceSquared(cellCenter, center);
                if (distanceSquared > reach * reach) continue; // saves the square root for the corners

                float profile = Profile(MathF.Sqrt(distanceSquared), radius, edge);
                if (profile <= 0f) continue;

                byte previous = SubVoxels.Get(working, sx, sy, sz);
                float wanted = add
                    ? MathF.Max(previous, profile * 255f)
                    : MathF.Min(previous, (1f - profile) * 255f);

                byte next = SubVoxels.Quantize(wanted);
                if (next == previous) continue;
                if (next > previous && SubIntersectsBox(playerBounds, bx, by, bz, sx, sy, sz)) continue; // do not build into the player

                SubVoxels.Set(working, sx, sy, sz, next);
                changed = true;
                if (add ? next >= SubVoxels.Iso : next < SubVoxels.Iso) crossesIso = true;
            }

            if (!changed) continue;

            // Only falloff fringe in a block that is still untouched: the shape itself does not
            // reach this far. Writing the density anyway merely shifts the neighbourhood's box
            // filter, and because a flat block surface sits exactly on the iso edge, that tips out
            // paper-thin skins or notches beside the shape.
            if (!crossesIso && !refined) continue;

            anyChange = true;

            if (!solid)
            {
                // Air gains substance, so create a block (SetLocal clears any old detail with it)
                chunk.SetLocal(lx, by, lz, blockId, WorldHeight);
                if (!SubVoxels.IsFull(working))
                    chunk.SetRefinement(lx, by, lz, working, WorldHeight);
            }
            else if (SubVoxels.IsEmpty(working))
            {
                chunk.SetLocal(lx, by, lz, BlockRegistry.Air, WorldHeight); // carved away completely
            }
            else
            {
                chunk.SetRefinement(lx, by, lz, working, WorldHeight); // a full field removes the entry itself
            }

            FireDirtyAround(cc, lx, lz, by);
        }

        return anyChange;
    }

    /// <summary>Fill 0..1 of the brush sphere: 1 at the core, exactly 0.5 (= iso) at distance == radius, 0 outside</summary>
    private static float Profile(float distance, float radius, float edge)
    {
        if (edge <= 0f) return distance <= radius ? 1f : 0f;

        float t = 0.5f + (radius - distance) / edge;
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        return t * t * (3f - 2f * t); // smoothstep: a falloff without a kink, or the brush edge shows
    }

    /// <summary>The cells of a block that lie within reach of the brush centre on one axis</summary>
    private static (int Min, int Max) CellRange(float center, float reach, int block)
    {
        int min = (int)MathF.Floor((center - reach - block) * SubVoxels.Divisions);
        int max = (int)MathF.Ceiling((center + reach - block) * SubVoxels.Divisions);

        return (Math.Max(0, min), Math.Min(SubVoxels.Divisions - 1, max));
    }

    private static float BlockDistanceSquared(int bx, int by, int bz, Vector3 point)
    {
        float dx = MathF.Max(0f, MathF.Max(bx - point.X, point.X - (bx + 1f)));
        float dy = MathF.Max(0f, MathF.Max(by - point.Y, point.Y - (by + 1f)));
        float dz = MathF.Max(0f, MathF.Max(bz - point.Z, point.Z - (bz + 1f)));
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool SubIntersectsBox(BoundingBox box, int bx, int by, int bz, int sx, int sy, int sz)
    {
        float minX = bx + sx * SubVoxels.CellSize;
        float minY = by + sy * SubVoxels.CellSize;
        float minZ = bz + sz * SubVoxels.CellSize;

        return box.Min.X < minX + SubVoxels.CellSize && box.Max.X > minX &&
               box.Min.Y < minY + SubVoxels.CellSize && box.Max.Y > minY &&
               box.Min.Z < minZ + SubVoxels.CellSize && box.Max.Z > minZ;
    }

    // --- Interaction ---

    private void UpdateHover(Camera3D camera, float buildReach)
    {
        _hasHover = false;
        _hasGhost = false;

        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, buildReach);
        if (hit.HasHit)
        {
            _hoverBlock = hit.Block;
            _hoverPlaceCell = hit.PlaceBlock;
            _hoverCenter = new Vector3(hit.Block.X + 0.5f, hit.Block.Y + 0.5f, hit.Block.Z + 0.5f);
            _hasHover = true;
            return;
        }

        // Nothing in the way: the build target sits in mid-air at the end of the reach
        Vector3 target = ray.Position + Vector3.Normalize(ray.Direction) * buildReach;
        int gx = (int)MathF.Floor(target.X);
        int gy = (int)MathF.Floor(target.Y);
        int gz = (int)MathF.Floor(target.Z);

        if (gy < 0 || gy >= WorldHeight) return;
        if (GetBlock(gx, gy, gz) != 0) return;

        _ghostCell = new VoxelRaycast.Vector3Int(gx, gy, gz);
        _ghostCenter = new Vector3(gx + 0.5f, gy + 0.5f, gz + 0.5f);
        _hasGhost = true;
    }

    private void TryRemove()
    {
        if (!_hasHover) return;

        int id = GetBlock(_hoverBlock.X, _hoverBlock.Y, _hoverBlock.Z);
        Color albedo = TerrainColors.ForBlock(id, _hoverBlock.X, _hoverBlock.Y, _hoverBlock.Z);

        SetBlock(_hoverBlock.X, _hoverBlock.Y, _hoverBlock.Z, BlockRegistry.Air);

        BlockBroken?.Invoke(_hoverCenter, albedo);
    }

    private void TryPlace(BoundingBox playerBounds)
    {
        // Looking at a block: build against its face; otherwise into mid-air at full reach
        VoxelRaycast.Vector3Int cell;
        if (_hasHover) cell = _hoverPlaceCell;
        else if (_hasGhost) cell = _ghostCell;
        else return;

        if (GetBlock(cell.X, cell.Y, cell.Z) != 0) return; // must be air
        if (IntersectsBlock(playerBounds, cell.X, cell.Y, cell.Z)) return; // do not build into the player

        SetBlock(cell.X, cell.Y, cell.Z, BuildMaterial);

        BlockPlaced?.Invoke(
            new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f),
            TerrainColors.ForBlock(BuildMaterial, cell.X, cell.Y, cell.Z));
    }

    private static bool IntersectsBlock(BoundingBox box, int x, int y, int z)
        => box.Min.X < x + 1 && box.Max.X > x &&
           box.Min.Y < y + 1 && box.Max.Y > y &&
           box.Min.Z < z + 1 && box.Max.Z > z;

    // --- World-level block access ---

    public int GetBlock(int wx, int wy, int wz)
    {
        if (wy < 0 || wy >= WorldHeight) return 0;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return 0;

        return chunk.GetLocal(lx, wy, lz, WorldHeight);
    }

    public bool TryGetRefinement(int wx, int wy, int wz, out byte[] field)
    {
        field = null!;
        if (wy < 0 || wy >= WorldHeight) return false;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return false;

        return chunk.TryGetRefinement(lx, wy, lz, WorldHeight, out field);
    }

    /// <summary>Block type of a sub-cell (world sub-coordinates, 8 per block); 0 = air</summary>
    public int GetSubVoxel(int swx, int swy, int swz)
    {
        // Shift and LowMask are FloorDiv and modulo for the power of two, negatives included
        int wx = swx >> SubVoxels.Shift;
        int wy = swy >> SubVoxels.Shift;
        int wz = swz >> SubVoxels.Shift;

        if (wy < 0 || wy >= WorldHeight) return 0;

        int id = GetBlock(wx, wy, wz);
        if (!BlockRegistry.IsSolid(id)) return 0;

        if (!TryGetRefinement(wx, wy, wz, out byte[] field)) return id; // full block
        return SubVoxels.IsSolid(field, swx & SubVoxels.LowMask, swy & SubVoxels.LowMask, swz & SubVoxels.LowMask) ? id : 0;
    }

    public void SetBlock(int wx, int wy, int wz, int id)
    {
        if (wy < 0 || wy >= WorldHeight) return;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return; // outside the loaded world

        chunk.SetLocal(lx, wy, lz, id, WorldHeight);

        FireDirtyAround(cc, lx, lz, wy);
    }

    // Edits on edges and corners also affect the meshes of the (diagonal) neighbours, for face culling and AO
    private void FireDirtyAround(ChunkCoord cc, int lx, int lz, int y)
    {
        ChunkDirty?.Invoke(cc, y, y, MeshReason.Edit);

        bool west = lx == 0;
        bool east = lx == Chunk.Size - 1;
        bool north = lz == 0;
        bool south = lz == Chunk.Size - 1;

        if (west) ChunkDirty?.Invoke(new ChunkCoord(cc.X - 1, cc.Z), y, y, MeshReason.Edit);
        if (east) ChunkDirty?.Invoke(new ChunkCoord(cc.X + 1, cc.Z), y, y, MeshReason.Edit);
        if (north) ChunkDirty?.Invoke(new ChunkCoord(cc.X, cc.Z - 1), y, y, MeshReason.Edit);
        if (south) ChunkDirty?.Invoke(new ChunkCoord(cc.X, cc.Z + 1), y, y, MeshReason.Edit);
        if (west && north) ChunkDirty?.Invoke(new ChunkCoord(cc.X - 1, cc.Z - 1), y, y, MeshReason.Edit);
        if (west && south) ChunkDirty?.Invoke(new ChunkCoord(cc.X - 1, cc.Z + 1), y, y, MeshReason.Edit);
        if (east && north) ChunkDirty?.Invoke(new ChunkCoord(cc.X + 1, cc.Z - 1), y, y, MeshReason.Edit);
        if (east && south) ChunkDirty?.Invoke(new ChunkCoord(cc.X + 1, cc.Z + 1), y, y, MeshReason.Edit);
    }

    // --- Coordinate helpers ---

    private static (ChunkCoord cc, int lx, int lz) WorldToChunk(int wx, int wz)
    {
        int cx = FloorDiv(wx, Chunk.Size);
        int cz = FloorDiv(wz, Chunk.Size);

        int lx = wx - cx * Chunk.Size;
        int lz = wz - cz * Chunk.Size;

        return (new ChunkCoord(cx, cz), lx, lz);
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((r > 0) != (b > 0))) q--;
        return q;
    }
}
