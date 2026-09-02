using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.MathTools;
using VoxelEngine.Rendering;

namespace VoxelEngine.World;

public class VoxelWorld
{
    public const int WorldHeight = 64;

    // Streaming: chunks are loaded in a circle around the player and unloaded with hysteresis
    // (LoadRadius * chunk size = 256 blocks, which is past the fog, so loading stays invisible)
    public const int LoadRadius = 8;
    public const int UnloadRadius = 10;

    private static readonly (int X, int Z)[] _loadOrder = BuildLoadOrder();

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();
    private readonly WorldStorage _storage;
    private readonly ITerrainGenerator _generator;

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
    private Vector3 _sculptTarget;
    private float _sculptRadiusForDraw;

    // The target moves between frames; without stamping along that stretch, a fast mouse movement
    // would leave a string of beads instead of a tube
    private bool _stroking;
    private Vector3 _strokePrevious;
    private bool _strokeAdding;
    private float _strokeParticleCooldown;
    private EngineSettings _settings = new();

    // While adding material the brush follows the crosshair at a limited pace. Otherwise the
    // surface races towards the view the moment the ray lands on freshly built material.
    private bool _hasBuildFront;
    private Vector3 _buildFront;
    private bool _rayHadHit;
    private float _rayLastDistance;

    // Where the brush actually bites. While adding, that trails the crosshair, and drawing the
    // preview at the crosshair instead would show the sphere somewhere the material never appears.
    private Vector3 _previewCenter;

    // The brush sphere and the block outline get in the way while building, so they only appear
    // briefly after a size change (wheel) and permanently while the debug overlay is on
    private float _previewTimer;

    private const float StrokeStepFactor = 0.5f;    // stamp spacing as a fraction of the radius
    private const int StrokeMaxStamps = 64;         // work budget for one frame of a fast stroke

    // A stroke is only broken when the target changes *depth*, which means the ray slid off an
    // edge onto something far away. Sideways speed, however high, is a hand movement and has to
    // draw a continuous line.
    private const float DepthJumpMeters = 1.5f;
    private const float DepthJumpFraction = 0.35f;
    private const float BuildMaxLag = 1.5f;         // how far the build front may lag, in radii
    private const float SculptParticleInterval = 0.07f;

    /// <summary>Switched with key V; see <see cref="TerrainMode"/></summary>
    public TerrainMode Mode { get; set; } = TerrainMode.Blocks;

    /// <summary>Both fine modes use the same sphere brush; only the presentation differs</summary>
    public bool UsesSculptTool => Mode != TerrainMode.Blocks;

    /// <summary>Chunk needs a new mesh (also fires for neighbours when an edit touches an edge)</summary>
    public event Action<ChunkCoord>? ChunkDirty;

    /// <summary>Block removed: centre plus albedo, for particles among others</summary>
    public event Action<Vector3, Color>? BlockBroken;

    /// <summary>Block placed: centre plus albedo</summary>
    public event Action<Vector3, Color>? BlockPlaced;

    /// <summary>Chunk was unloaded, so its mesh can go</summary>
    public event Action<ChunkCoord>? ChunkUnloaded;

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public int LoadedChunkCount => _chunks.Count;

    public VoxelWorld(WorldStorage storage, ITerrainGenerator? generator = null)
    {
        _storage = storage;
        _generator = generator ?? new DefaultTerrainGenerator();
    }

    public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        => _chunks.TryGetValue(coord, out chunk!);

    // --- Streaming ---

    /// <summary>Loads everything within the radius right away (blocking), for the spawn area at startup</summary>
    public void EnsureAround(Vector3 position, int radiusChunks)
    {
        (int pcx, int pcz) = PositionToChunk(position);

        for (int dz = -radiusChunks; dz <= radiusChunks; dz++)
        for (int dx = -radiusChunks; dx <= radiusChunks; dx++)
        {
            var coord = new ChunkCoord(pcx + dx, pcz + dz);
            if (!_chunks.ContainsKey(coord)) LoadChunk(coord);
        }
    }

    /// <summary>Call once per frame: loads the next missing chunks within budget and drops distant ones</summary>
    public void UpdateStreaming(Vector3 playerPosition, int loadBudget)
    {
        (int pcx, int pcz) = PositionToChunk(playerPosition);

        List<ChunkCoord>? toUnload = null;
        foreach (ChunkCoord coord in _chunks.Keys)
        {
            int dx = coord.X - pcx;
            int dz = coord.Z - pcz;
            if (dx * dx + dz * dz <= UnloadRadius * UnloadRadius) continue;
            (toUnload ??= new List<ChunkCoord>()).Add(coord);
        }

        if (toUnload != null)
            foreach (ChunkCoord coord in toUnload)
                UnloadChunk(coord);

        foreach ((int offsetX, int offsetZ) in _loadOrder)
        {
            if (loadBudget <= 0) break;

            var coord = new ChunkCoord(pcx + offsetX, pcz + offsetZ);
            if (_chunks.ContainsKey(coord)) continue;

            LoadChunk(coord);
            loadBudget--;
        }
    }

    /// <summary>Manual save: the entire current state of the world becomes the save</summary>
    public bool SaveWorld()
    {
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

    /// <summary>Manual load: discards the current state and restores the save</summary>
    public bool LoadWorld()
    {
        // Reject old or foreign format versions, or a fresh world would quietly be loaded instead
        if (!_storage.HasCompatibleSave) return false;

        _keptModified.Clear();
        _diskIsBase = true;

        // Drop everything loaded; afterwards it comes fresh from disk or from the generator
        foreach (ChunkCoord coord in _chunks.Keys.ToList())
        {
            _chunks.Remove(coord);
            ChunkUnloaded?.Invoke(coord);
        }

        return true;
    }

    private void LoadChunk(ChunkCoord coord)
    {
        Chunk chunk;
        if (_keptModified.Remove(coord, out Chunk? kept))
        {
            chunk = kept;
        }
        else if (_diskIsBase && _storage.TryLoad(coord, Chunk.Size * WorldHeight * Chunk.Size, out byte[]? blocks, out Dictionary<int, byte[]>? refinements))
        {
            chunk = new Chunk(coord, blocks!, refinements!);
        }
        else
        {
            chunk = new Chunk(coord, WorldHeight, _generator);
        }

        _chunks.Add(coord, chunk);

        // Remesh this chunk and all 8 neighbours: faces towards previously "empty" neighbouring
        // space disappear, and ambient occlusion at the borders is only right with neighbour data
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            ChunkDirty?.Invoke(new ChunkCoord(coord.X + dx, coord.Z + dz));
    }

    private void UnloadChunk(ChunkCoord coord)
    {
        if (!_chunks.Remove(coord, out Chunk? chunk)) return;

        if (chunk.Modified) _keptModified[coord] = chunk;
        ChunkUnloaded?.Invoke(coord);
    }

    private static (int cx, int cz) PositionToChunk(Vector3 position)
        => (FloorDiv((int)MathF.Floor(position.X), Chunk.Size),
            FloorDiv((int)MathF.Floor(position.Z), Chunk.Size));

    private static (int X, int Z)[] BuildLoadOrder()
    {
        var offsets = new List<(int X, int Z)>();
        for (int dz = -LoadRadius; dz <= LoadRadius; dz++)
        for (int dx = -LoadRadius; dx <= LoadRadius; dx++)
            if (dx * dx + dz * dz <= LoadRadius * LoadRadius)
                offsets.Add((dx, dz));

        offsets.Sort((a, b) => (a.X * a.X + a.Z * a.Z).CompareTo(b.X * b.X + b.Z * b.Z));
        return offsets.ToArray();
    }

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

    public void DrawHover()
    {
        // Fade out at the end instead of cutting: a jump is more noticeable than the preview itself
        const float fadeSeconds = 0.35f;
        float visibility = ShowPreviewAlways ? 1f : Math.Clamp(_previewTimer / fadeSeconds, 0f, 1f);
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
        Raylib.DrawSphere(_previewCenter, radius, Fade(tint, (active ? 60 : 34) / 255f * visibility));
        Raylib.EndBlendMode();

        Raylib.DrawSphereWires(_previewCenter, radius, 12, 12, Fade(tint, (active ? 200 : 150) / 255f * visibility));
    }

    // --- Sculpt-Modus ---

    private void UpdateSculpt(Camera3D camera, BoundingBox playerBounds, float buildReach, float sculptRadius)
    {
        _sculptRadiusForDraw = sculptRadius;
        _hasSculptTarget = false;

        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        // Raycast at sub-voxel resolution (coordinates x8), so it also reads drilled holes correctly
        var hit = VoxelRaycast.Cast(
            GetSubVoxel,
            ray.Position * SubVoxels.Divisions,
            ray.Direction,
            buildReach * SubVoxels.Divisions);

        if (hit.HasHit)
        {
            _sculptTarget = new Vector3(
                (hit.Block.X + 0.5f) * SubVoxels.CellSize,
                (hit.Block.Y + 0.5f) * SubVoxels.CellSize,
                (hit.Block.Z + 0.5f) * SubVoxels.CellSize);
        }
        else
        {
            // nothing hit: shape mid-air at the end of the reach
            _sculptTarget = ray.Position + Vector3.Normalize(ray.Direction) * buildReach;
        }
        _hasSculptTarget = true;
        _previewCenter = _sculptTarget;

        // Track the target's depth every frame, button held or not. Digging and building move the
        // surface by at most a brush radius per frame, so only a real edge trips the threshold.
        float hitDistance = hit.HasHit ? hit.Distance / SubVoxels.Divisions : buildReach;
        bool rayJumped =
            hit.HasHit != _rayHadHit ||
            MathF.Abs(hitDistance - _rayLastDistance) > MathF.Max(DepthJumpMeters, hitDistance * DepthJumpFraction);

        _rayHadHit = hit.HasHit;
        _rayLastDistance = hitDistance;

        bool carve = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool build = Raylib.IsMouseButtonDown(MouseButton.Right);

        if (!carve && !build)
        {
            _stroking = false;
            _hasBuildFront = false;
            return;
        }

        bool add = !carve && build; // with both buttons down, carving wins
        if (_stroking && add != _strokeAdding) _stroking = false;

        float frameTime = Raylib.GetFrameTime();
        _strokeAdding = add;
        _strokeParticleCooldown -= frameTime;

        // Hard-edged Sculpt mode switches cells outright, or the cube look frays; Smooth lays down
        // a soft falloff, which is what marching cubes turns into a rounded surface
        float edge = Mode == TerrainMode.Smooth ? sculptRadius * _settings.BrushSoftness : 0f;

        // The target jumped to another surface without the hand doing anything: start over there,
        // otherwise the stroke drags a tube across the gap in between.
        if (rayJumped) _hasBuildFront = false;

        // Carving acts on the target immediately, since any lag gets in the way while digging.
        // Adding creeps towards it so the surface grows steadily instead of jumping by whole spheres.
        Vector3 point = add ? AdvanceBuildFront(_sculptTarget, sculptRadius, frameTime) : _sculptTarget;
        if (!add) _hasBuildFront = false;

        _previewCenter = point;

        bool changed = StampStroke(point, sculptRadius, add, edge, playerBounds, rayJumped);

        _strokePrevious = point;
        _stroking = true;

        if (changed && _strokeParticleCooldown <= 0f)
        {
            _strokeParticleCooldown = SculptParticleInterval;
            Color albedo = TerrainColors.ForBlock(
                BlockRegistry.Terrain,
                (int)MathF.Floor(_sculptTarget.X), (int)MathF.Floor(_sculptTarget.Y), (int)MathF.Floor(_sculptTarget.Z));

            if (add) BlockPlaced?.Invoke(_sculptTarget, albedo);
            else BlockBroken?.Invoke(_sculptTarget, albedo);
        }
    }

    /// <summary>
    /// The build front trails the crosshair at the configured pace. Move the mouse faster and you
    /// outrun it: it then visibly stays behind instead of snapping to the target and filling in the
    /// skipped stretch within one frame. It never falls back further than
    /// <see cref="BuildMaxLag"/> radii, or you would be building blind.
    /// </summary>
    private Vector3 AdvanceBuildFront(Vector3 target, float radius, float frameTime)
    {
        if (!_hasBuildFront)
        {
            _hasBuildFront = true;
            _buildFront = target;
            return _buildFront;
        }

        Vector3 delta = target - _buildFront;
        float distance = delta.Length();
        if (distance < 1e-4f) return _buildFront;

        float step = MathF.Max(0.01f, _settings.BuildSpeed) * frameTime;
        float pull = MathF.Max(step, distance - radius * BuildMaxLag);

        _buildFront = pull >= distance ? target : _buildFront + delta * (pull / distance);

        return _buildFront;
    }

    /// <summary>
    /// Stamps the brush along the stretch covered since the last frame. A single stamp per frame
    /// would leave a string of beads when the mouse moves fast; overlapping spheres give a
    /// continuous tube instead.
    /// </summary>
    private bool StampStroke(Vector3 target, float radius, bool add, float edge, BoundingBox playerBounds, bool jumped)
    {
        bool changed = SculptBlob(target, radius, add, edge, BlockRegistry.Stone, playerBounds);

        // A jump means the two positions are on different surfaces; joining them would draw a
        // bridge through the air that nobody asked for
        if (!_stroking || jumped) return changed;

        Vector3 delta = target - _strokePrevious;
        float distance = delta.Length();

        float step = radius * StrokeStepFactor;
        if (distance < step) return changed;

        // Past the budget the stamps spread out instead of the segment being dropped: a slightly
        // coarser line still beats a hole in the stroke
        int stamps = Math.Min((int)(distance / step), StrokeMaxStamps);

        for (int i = 1; i <= stamps; i++)
        {
            Vector3 point = _strokePrevious + delta * (i / (float)(stamps + 1));
            changed |= SculptBlob(point, radius, add, edge, BlockRegistry.Stone, playerBounds);
        }

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

            FireDirtyAround(cc, lx, lz);
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

        SetBlock(cell.X, cell.Y, cell.Z, BlockRegistry.Stone);

        BlockPlaced?.Invoke(
            new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f),
            TerrainColors.ForBlock(BlockRegistry.Stone, cell.X, cell.Y, cell.Z));
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

        FireDirtyAround(cc, lx, lz);
    }

    // Edits on edges and corners also affect the meshes of the (diagonal) neighbours, for face culling and AO
    private void FireDirtyAround(ChunkCoord cc, int lx, int lz)
    {
        ChunkDirty?.Invoke(cc);

        bool west = lx == 0;
        bool east = lx == Chunk.Size - 1;
        bool north = lz == 0;
        bool south = lz == Chunk.Size - 1;

        if (west) ChunkDirty?.Invoke(new ChunkCoord(cc.X - 1, cc.Z));
        if (east) ChunkDirty?.Invoke(new ChunkCoord(cc.X + 1, cc.Z));
        if (north) ChunkDirty?.Invoke(new ChunkCoord(cc.X, cc.Z - 1));
        if (south) ChunkDirty?.Invoke(new ChunkCoord(cc.X, cc.Z + 1));
        if (west && north) ChunkDirty?.Invoke(new ChunkCoord(cc.X - 1, cc.Z - 1));
        if (west && south) ChunkDirty?.Invoke(new ChunkCoord(cc.X - 1, cc.Z + 1));
        if (east && north) ChunkDirty?.Invoke(new ChunkCoord(cc.X + 1, cc.Z - 1));
        if (east && south) ChunkDirty?.Invoke(new ChunkCoord(cc.X + 1, cc.Z + 1));
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
