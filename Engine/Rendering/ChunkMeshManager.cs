using Raylib_cs;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using VoxelEngine.Core;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Keeps the GPU meshes of the loaded chunks and rebuilds them in the background when something
/// changes. Snapshot and upload run on the main thread (GL context), the meshing itself on worker
/// threads; the old mesh stays visible until the new one is ready.
///
/// Near the camera a chunk is a stack of <see cref="SectionCount"/> sections at full detail, so a
/// brush stroke only rebuilds the sections it touched. Further out a column is one mesh built from
/// a downsampled grid (2 m, then 4 m blocks), which is what lets the view reach a kilometre and
/// more: the far field costs a few hundred triangles per column instead of tens of thousands.
///
/// Work is pulled, not pushed: every frame the few most urgent chunks are snapshotted and handed
/// to the workers, edits first and then nearest first, so a stroke never waits behind a mode
/// switch and a mode switch spreads outward from the player like a wave.
/// </summary>
public sealed class ChunkMeshManager : IDisposable
{
    /// <summary>Blocks per meshing section at full detail; <see cref="VoxelWorld.WorldHeight"/> must divide by it</summary>
    public const int SectionHeight = 16;

    public const int SectionCount = VoxelWorld.WorldHeight / SectionHeight;

    private const int AllSections = (1 << SectionCount) - 1;

    /// <summary>Main-thread time per frame for taking snapshots and for uploading meshes</summary>
    private const double SnapshotBudgetMs = 2.5;
    private const double UploadBudgetMs = 3.0;

    private sealed class Entry
    {
        /// <summary>Level of detail of the meshes on the GPU; -1 before the first upload</summary>
        public int Lod = -1;

        /// <summary>Mesh parts per section at level 0; at coarser levels only slot 0 is used</summary>
        public readonly GpuMesh[]?[] Sections = new GpuMesh[]?[SectionCount];

        /// <summary>Sequence of the job whose result each section shows; a lower one arriving later is dropped</summary>
        public readonly int[] AppliedSequence = new int[SectionCount];

        /// <summary>Sections that received an upload since the last level change</summary>
        public int MeshedMask;

        /// <summary>Of those, the ones built for the smooth presentation</summary>
        public int SmoothMask;

        public int VertexCount;
        public int TriangleCount;
        public int Bytes;

        public bool HasMeshes => MeshedMask != 0;
    }

    private readonly record struct MeshJob(
        ChunkCoord Coord, Chunk Identity, int Lod, int Sections, byte[] Grid, Dictionary<int, byte[]> Refinements,
        int WorldX, int WorldZ, int Sequence, bool Smooth);

    private sealed record MeshResult(
        ChunkCoord Coord, Chunk Identity, int Lod, int Sections, ChunkMeshData[]?[] Data, int Sequence, bool Smooth, bool Skipped);

    private static readonly Dictionary<int, byte[]> NoRefinements = new();

    private readonly VoxelWorld _world;
    private readonly Dictionary<ChunkCoord, Entry> _entries = new();
    private readonly MeshScheduler _scheduler = new();
    private readonly Dictionary<ChunkCoord, int> _sequence = new();
    private readonly Queue<MeshResult> _held = new();

    private readonly BlockingCollection<MeshJob> _jobs = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    private readonly Thread[] _workers;
    private volatile bool _smooth;
    private volatile bool _disposing;

    private readonly Chunk?[] _neighbourhood = new Chunk?[9];
    private Vector3 _camera;

    /// <summary>Marching-cubes presentation instead of hard-edged cubes (Smooth mode)</summary>
    public bool SmoothRendering => _smooth;

    /// <summary>Radius in chunks around the camera that stays at full detail</summary>
    public float DetailRadius { get; set; } = 8f;

    public int WorkerCount => _workers.Length;
    public int VisibleSections { get; private set; }
    public int DrawCalls { get; private set; }
    public int MeshedChunks => _entries.Count;
    /// <summary>Chunks that will be meshed: waiting in the queue with their neighbours present, or in flight</summary>
    public int PendingChunks => _scheduler.CountReady(_world.IsMeshable) + _scheduler.InFlightCount;

    public int InFlightChunks => _scheduler.InFlightCount;
    public int TotalVertices { get; private set; }
    public int TotalTriangles { get; private set; }
    public long GpuBytes { get; private set; }

    public ChunkMeshManager(VoxelWorld world)
    {
        _world = world;
        _world.ChunkDirty += MarkDirty;
        _world.ChunkUnloaded += OnChunkUnloaded;

        // Chunks loaded before this manager existed announced themselves to nobody
        foreach (Chunk chunk in _world.Chunks)
            if (chunk.Surrounded) _scheduler.Mark(chunk.Coord, AllSections, MeshReason.Stream);

        // The main thread and the driver keep two cores; the rest mesh. They run below normal
        // priority so a burst of work never steals a frame from the game.
        int workerCount = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ChunkMesher{i}",
                Priority = ThreadPriority.BelowNormal,
            };
            _workers[i].Start();
        }
    }

    /// <summary>An edit between <paramref name="minY"/> and <paramref name="maxY"/> dirties the sections it spans</summary>
    private void MarkDirty(ChunkCoord coord, int minY, int maxY, MeshReason reason)
    {
        // Widened by one block: a section's outermost blocks read the neighbourhood beyond it, so
        // an edit right at a section border changes the mesh on both sides
        int first = Math.Clamp((minY - 1) / SectionHeight, 0, SectionCount - 1);
        int last = Math.Clamp((maxY + 1) / SectionHeight, 0, SectionCount - 1);

        int mask = 0;
        for (int section = first; section <= last; section++)
            mask |= 1 << section;

        _scheduler.Mark(coord, mask, reason);
    }

    /// <summary>Switch the presentation; every loaded chunk is remeshed, nearest first</summary>
    public void SetSmoothRendering(bool smooth)
    {
        if (smooth == _smooth) return;

        _smooth = smooth;

        // Jobs in flight for the old look are skipped by the workers; marking every chunk brings
        // their sections back into the queue
        foreach (Chunk chunk in _world.Chunks)
            _scheduler.Mark(chunk.Coord, AllSections, MeshReason.Mode);
    }

    private void OnChunkUnloaded(ChunkCoord coord)
    {
        _scheduler.Forget(coord);
        _sequence.Remove(coord);

        if (!_entries.Remove(coord, out Entry? entry)) return;

        FreeAll(entry);
    }

    private static void FreeAll(Entry entry)
    {
        for (int section = 0; section < SectionCount; section++)
            FreeSection(entry, section);

        entry.MeshedMask = 0;
        entry.SmoothMask = 0;
        entry.VertexCount = 0;
        entry.TriangleCount = 0;
        entry.Bytes = 0;
        Array.Clear(entry.AppliedSequence);
    }

    private static void FreeSection(Entry entry, int section)
    {
        GpuMesh[]? parts = entry.Sections[section];
        if (parts == null) return;

        foreach (GpuMesh part in parts)
        {
            entry.VertexCount -= part.VertexCount;
            entry.TriangleCount -= part.TriangleCount;
            entry.Bytes -= part.Bytes;
            part.Dispose();
        }

        entry.Sections[section] = null;
    }

    /// <summary>
    /// Meshes every chunk that can be meshed, synchronously; done once at startup so the spawn
    /// area stands complete on the first frame instead of building up over the first second.
    /// </summary>
    public void BuildAllNow(Vector3 camera)
    {
        _camera = camera;

        var builder = new MeshBuilder();
        Span<(ChunkCoord Coord, int Sections, MeshReason Reason)> picks = stackalloc (ChunkCoord, int, MeshReason)[1];

        while (_scheduler.Take(1, _world.IsMeshable, DistanceInChunks, picks) == 1)
        {
            MeshJob job = Snapshot(picks[0].Coord, picks[0].Sections);
            _scheduler.BeginJob(job.Coord);

            MeshResult result = Build(job, builder);
            Apply(result);
        }
    }

    /// <summary>
    /// Once per frame: uploads finished meshes, re-checks the level of detail of every column
    /// against the camera and hands the most urgent dirty chunks to the workers.
    /// </summary>
    public void Update(Vector3 camera, FrameProfiler? profiler = null)
    {
        _camera = camera;

        long started = Stopwatch.GetTimestamp();
        int uploaded = DrainResults();
        profiler?.Add(FrameSlot.Upload, started, uploaded);

        RefreshLevels();

        started = Stopwatch.GetTimestamp();
        int snapshots = Dispatch();
        profiler?.Add(FrameSlot.Snapshot, started, snapshots);
    }

    private int DrainResults()
    {
        while (_results.TryDequeue(out MeshResult? result))
            _held.Enqueue(result);

        // Skipped and stale results cost nothing; only uploads count against the budget, and the
        // old mesh keeps showing until the frame after has room
        long started = Stopwatch.GetTimestamp();
        int applied = 0;

        while (_held.Count > 0)
        {
            if (Elapsed(started) > UploadBudgetMs) break;

            Apply(_held.Dequeue());
            applied++;
        }

        return applied;
    }

    private void Apply(MeshResult result)
    {
        _scheduler.EndJob(result.Coord);

        if (result.Skipped) return;
        if (result.Smooth != _smooth) return; // presentation switched meanwhile; everything was re-marked
        if (!_world.TryGetChunk(result.Coord, out Chunk chunk) || !ReferenceEquals(chunk, result.Identity)) return;

        Entry entry = EntryFor(result.Coord);

        // Built for a level the column has since left: rebuild at the level it wants now, or a
        // column that was never uploaded would stay a hole
        if (result.Lod != LodPolicy.Choose(entry.Lod, DistanceInChunks(result.Coord), DetailRadius))
        {
            _scheduler.Mark(result.Coord, AllSections, MeshReason.Lod);
            return;
        }

        if (entry.Lod != result.Lod)
        {
            // A level change replaces the whole column at once, or the two levels would show
            // side by side inside one chunk
            bool wholeColumn = result.Lod > 0 || result.Sections == AllSections;
            if (!wholeColumn)
            {
                _scheduler.Mark(result.Coord, AllSections, MeshReason.Lod);
                return;
            }

            FreeAll(entry);
            entry.Lod = result.Lod;
        }

        for (int section = 0; section < SectionCount; section++)
        {
            if ((result.Sections & (1 << section)) == 0) continue;
            if (result.Sequence <= entry.AppliedSequence[section]) continue; // a newer job already landed

            entry.AppliedSequence[section] = result.Sequence;
            Upload(entry, section, result.Data[section], result.Smooth);
        }
    }

    private static void Upload(Entry entry, int section, ChunkMeshData[]? data, bool smooth)
    {
        FreeSection(entry, section);

        int bit = 1 << section;
        entry.MeshedMask |= bit;
        if (smooth) entry.SmoothMask |= bit;
        else entry.SmoothMask &= ~bit;

        if (data == null || data.Length == 0) return;

        var parts = new GpuMesh[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            ChunkMeshData part = data[i];
            parts[i] = new GpuMesh(part.Vertices.AsSpan(0, part.VertexCount), part.Indices.AsSpan(0, part.IndexCount));

            entry.VertexCount += parts[i].VertexCount;
            entry.TriangleCount += parts[i].TriangleCount;
            entry.Bytes += parts[i].Bytes;
        }

        entry.Sections[section] = parts;
    }

    private Entry EntryFor(ChunkCoord coord)
    {
        if (_entries.TryGetValue(coord, out Entry? entry)) return entry;

        entry = new Entry();
        _entries[coord] = entry;

        return entry;
    }

    /// <summary>A column that crossed a level border is rebuilt at the new level</summary>
    private void RefreshLevels()
    {
        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            if (entry.Lod < 0) continue;

            int wanted = LodPolicy.Choose(entry.Lod, DistanceInChunks(coord), DetailRadius);
            if (wanted != entry.Lod) _scheduler.Mark(coord, AllSections, MeshReason.Lod);
        }
    }

    private int Dispatch()
    {
        // Never queue more than the workers can start soon: ordering is decided late, with the
        // camera where it is now, and a stale job costs nothing to skip
        int slots = _workers.Length * 2 - _scheduler.InFlightCount;
        if (slots <= 0) return 0;

        Span<(ChunkCoord Coord, int Sections, MeshReason Reason)> picks = stackalloc (ChunkCoord, int, MeshReason)[16];
        int count = _scheduler.Take(Math.Min(slots, picks.Length), _world.IsMeshable, DistanceInChunks, picks);

        long started = Stopwatch.GetTimestamp();
        int dispatched = 0;

        for (int i = 0; i < count; i++)
        {
            (ChunkCoord coord, int sections, MeshReason reason) = picks[i];

            // Edits always go out this frame; the rest waits when snapshots ate the budget
            if (reason != MeshReason.Edit && Elapsed(started) > SnapshotBudgetMs)
            {
                _scheduler.Mark(coord, sections, reason);
                continue;
            }

            MeshJob job = Snapshot(coord, sections);
            _scheduler.BeginJob(coord);
            _jobs.Add(job);
            dispatched++;
        }

        return dispatched;
    }

    private static double Elapsed(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private float DistanceInChunks(ChunkCoord coord)
    {
        float dx = coord.X + 0.5f - _camera.X / Chunk.Size;
        float dz = coord.Z + 0.5f - _camera.Z / Chunk.Size;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>The blocks a job needs, copied on the main thread; the workers never touch the world</summary>
    private MeshJob Snapshot(ChunkCoord coord, int sections)
    {
        Chunk chunk = _world.TryGetChunk(coord, out Chunk found) ? found : throw new InvalidOperationException("snapshot of an unloaded chunk");
        _world.GetNeighbourhood(coord, _neighbourhood);

        int sequence = (_sequence.TryGetValue(coord, out int last) ? last : 0) + 1;
        _sequence[coord] = sequence;

        int worldX = (int)chunk.WorldPosition.X;
        int worldZ = (int)chunk.WorldPosition.Z;

        int lod = LodPolicy.Choose(_entries.TryGetValue(coord, out Entry? entry) ? entry.Lod : -1, DistanceInChunks(coord), DetailRadius);

        if (lod > 0)
        {
            int scale = LodPolicy.ScaleOf(lod);
            byte[] coarse = ArrayPool<byte>.Shared.Rent(MeshGrid.LengthFor(Chunk.Size / scale, VoxelWorld.WorldHeight / scale));
            LodSampler.Sample(_neighbourhood, VoxelWorld.WorldHeight, scale, coarse);

            return new MeshJob(coord, chunk, lod, 1, coarse, NoRefinements, worldX, worldZ, sequence, _smooth);
        }

        (int yStart, int yEnd) = RowsOf(sections);
        byte[] padded = ArrayPool<byte>.Shared.Rent(ChunkMesher.PaddedLength(VoxelWorld.WorldHeight));

        CopyBand(padded, yStart - 1, yEnd + 1);
        Dictionary<int, byte[]> refinements = SnapshotRefinements(yStart - 1, yEnd + 1);

        return new MeshJob(coord, chunk, 0, sections, padded, refinements, worldX, worldZ, sequence, _smooth);
    }

    /// <summary>The block rows [start, end) spanned by a section mask</summary>
    private static (int Start, int End) RowsOf(int sections)
    {
        int first = SectionCount, last = -1;
        for (int section = 0; section < SectionCount; section++)
        {
            if ((sections & (1 << section)) == 0) continue;
            first = Math.Min(first, section);
            last = Math.Max(last, section);
        }

        return (first * SectionHeight, (last + 1) * SectionHeight);
    }

    /// <summary>
    /// Fills the rows [minY, maxY] of the padded array: the chunk's own blocks plus a one-block
    /// shell copied straight out of the neighbours. Rows below the world count as solid, so the
    /// never-visible underside produces no mesh; rows above it are air.
    /// </summary>
    private void CopyBand(byte[] padded, int minY, int maxY)
    {
        const int worldHeight = VoxelWorld.WorldHeight;
        const int stride = ChunkMesher.PaddedSize;

        Chunk chunk = _neighbourhood[4]!;
        Chunk? west = _neighbourhood[3], east = _neighbourhood[5], north = _neighbourhood[1], south = _neighbourhood[7];
        Chunk? northWest = _neighbourhood[0], northEast = _neighbourhood[2], southWest = _neighbourhood[6], southEast = _neighbourhood[8];

        for (int y = Math.Max(-1, minY); y <= Math.Min(worldHeight, maxY); y++)
        {
            int rowStart = ChunkMesher.Index(-1, y, -1);

            if (y < 0)
            {
                Array.Fill(padded, BlockRegistry.Terrain, rowStart, stride * stride);
                continue;
            }

            if (y >= worldHeight)
            {
                Array.Fill(padded, BlockRegistry.Air, rowStart, stride * stride);
                continue;
            }

            for (int z = 0; z < Chunk.Size; z++)
                chunk.CopyRow(y, z, padded, ChunkMesher.Index(0, y, z));

            CopyShellRow(north, y, Chunk.Size - 1, padded, ChunkMesher.Index(0, y, -1));
            CopyShellRow(south, y, 0, padded, ChunkMesher.Index(0, y, Chunk.Size));
            CopyShellColumn(west, y, Chunk.Size - 1, padded, ChunkMesher.Index(-1, y, 0), stride);
            CopyShellColumn(east, y, 0, padded, ChunkMesher.Index(Chunk.Size, y, 0), stride);

            padded[ChunkMesher.Index(-1, y, -1)] = Corner(northWest, Chunk.Size - 1, y, Chunk.Size - 1);
            padded[ChunkMesher.Index(Chunk.Size, y, -1)] = Corner(northEast, 0, y, Chunk.Size - 1);
            padded[ChunkMesher.Index(-1, y, Chunk.Size)] = Corner(southWest, Chunk.Size - 1, y, 0);
            padded[ChunkMesher.Index(Chunk.Size, y, Chunk.Size)] = Corner(southEast, 0, y, 0);
        }
    }

    private static void CopyShellRow(Chunk? neighbour, int y, int z, byte[] padded, int destination)
    {
        if (neighbour != null) neighbour.CopyRow(y, z, padded, destination);
        else Array.Fill(padded, BlockRegistry.Air, destination, Chunk.Size);
    }

    private static void CopyShellColumn(Chunk? neighbour, int y, int x, byte[] padded, int destination, int stride)
    {
        if (neighbour != null)
        {
            neighbour.CopyColumn(y, x, padded, destination, stride);
            return;
        }

        for (int z = 0; z < Chunk.Size; z++)
            padded[destination + z * stride] = BlockRegistry.Air;
    }

    private static byte Corner(Chunk? neighbour, int x, int y, int z)
        => neighbour == null ? BlockRegistry.Air : (byte)neighbour.GetLocal(x, y, z, VoxelWorld.WorldHeight);

    /// <summary>
    /// The chunk's sub-voxel density fields within the rows, rekeyed to padded indices, plus the
    /// facing border fields of the neighbours. Most of the world has never been carved, and then
    /// this is one shared empty dictionary.
    /// </summary>
    private Dictionary<int, byte[]> SnapshotRefinements(int minY, int maxY)
    {
        bool any = false;
        foreach (Chunk? chunk in _neighbourhood)
            if (chunk != null && chunk.RefinementCount > 0) any = true;

        if (!any) return NoRefinements;

        var refinements = new Dictionary<int, byte[]>();

        for (int slot = 0; slot < 9; slot++)
        {
            Chunk? chunk = _neighbourhood[slot];
            if (chunk == null || chunk.RefinementCount == 0) continue;

            int dx = slot % 3 - 1;
            int dz = slot / 3 - 1;

            foreach ((int index, byte[] field) in chunk.Refinements)
            {
                (int x, int y, int z) = Chunk.DecodeIndex(index);
                if (y < minY || y > maxY) continue;

                // Of a neighbour only the blocks touching this chunk matter
                if (dx == -1 && x != Chunk.Size - 1) continue;
                if (dx == 1 && x != 0) continue;
                if (dz == -1 && z != Chunk.Size - 1) continue;
                if (dz == 1 && z != 0) continue;

                refinements[ChunkMesher.Index(x + dx * Chunk.Size, y, z + dz * Chunk.Size)] = field;
            }
        }

        return refinements;
    }

    [ThreadStatic] private static MeshBuilder? _builder;

    private void WorkerLoop()
    {
        MeshBuilder builder = _builder ??= new MeshBuilder();

        foreach (MeshJob job in _jobs.GetConsumingEnumerable())
            _results.Enqueue(Build(job, builder));
    }

    private MeshResult Build(MeshJob job, MeshBuilder builder)
    {
        var data = new ChunkMeshData[]?[SectionCount];

        // A job for the old presentation is not worth finishing; its sections are already back
        // in the queue with the new one
        bool skipped = _disposing || job.Smooth != _smooth;

        if (!skipped)
        {
            if (job.Lod == 0)
            {
                var grid = MeshGrid.FullDetail(job.Grid, VoxelWorld.WorldHeight);

                for (int section = 0; section < SectionCount; section++)
                {
                    if ((job.Sections & (1 << section)) == 0) continue;

                    int yStart = section * SectionHeight;
                    data[section] = job.Smooth
                        ? SmoothChunkMesher.Build(builder, in grid, job.Refinements, job.WorldX, job.WorldZ, yStart, yStart + SectionHeight)
                        : ChunkMesher.Build(builder, in grid, job.Refinements, job.WorldX, job.WorldZ, yStart, yStart + SectionHeight);
                }
            }
            else
            {
                var grid = MeshGrid.Coarse(job.Grid, VoxelWorld.WorldHeight, LodPolicy.ScaleOf(job.Lod));

                data[0] = job.Smooth
                    ? SmoothChunkMesher.Build(builder, in grid, NoRefinements, job.WorldX, job.WorldZ, 0, grid.Height)
                    : ChunkMesher.Build(builder, in grid, NoRefinements, job.WorldX, job.WorldZ, 0, grid.Height);
            }
        }

        ArrayPool<byte>.Shared.Return(job.Grid);

        return new MeshResult(job.Coord, job.Identity, job.Lod, job.Sections, data, job.Sequence, job.Smooth, skipped);
    }

    /// <summary>
    /// Draws every visible mesh with the shader bound once. Columns beyond <paramref name="maxDistance"/>
    /// metres are skipped before the frustum test: past the fog they are sky-coloured anyway.
    /// </summary>
    public void Draw(Shader shader, Frustum frustum, Vector3 camera, float maxDistance)
    {
        VisibleSections = 0;
        DrawCalls = 0;
        TotalVertices = 0;
        TotalTriangles = 0;
        GpuBytes = 0;

        float maxDistanceSquared = maxDistance * maxDistance;

        MeshDrawer.Begin(shader);

        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            TotalVertices += entry.VertexCount;
            TotalTriangles += entry.TriangleCount;
            GpuBytes += entry.Bytes;

            if (!entry.HasMeshes) continue;

            float originX = coord.X * Chunk.Size;
            float originZ = coord.Z * Chunk.Size;

            if (DistanceSquaredToColumn(camera, originX, originZ) > maxDistanceSquared) continue;

            // Vertices are in chunk-local metres, so one translation carries every section
            Matrix4x4 transform = Raymath.MatrixTranslate(originX, 0, originZ);

            if (entry.Lod == 0)
            {
                for (int section = 0; section < SectionCount; section++)
                {
                    GpuMesh[]? parts = entry.Sections[section];
                    if (parts == null) continue;

                    var min = new Vector3(originX, section * SectionHeight, originZ);
                    var max = min + new Vector3(Chunk.Size, SectionHeight, Chunk.Size);
                    if (!frustum.Intersects(min, max)) continue;

                    DrawParts(parts, transform);
                }
            }
            else if (entry.Sections[0] is { } parts)
            {
                var min = new Vector3(originX, 0, originZ);
                var max = min + new Vector3(Chunk.Size, VoxelWorld.WorldHeight, Chunk.Size);
                if (!frustum.Intersects(min, max)) continue;

                DrawParts(parts, transform);
            }
        }

        MeshDrawer.End();
    }

    private void DrawParts(GpuMesh[] parts, Matrix4x4 transform)
    {
        VisibleSections++;

        foreach (GpuMesh part in parts)
        {
            MeshDrawer.Draw(part, transform);
            DrawCalls++;
        }
    }

    private static float DistanceSquaredToColumn(Vector3 camera, float originX, float originZ)
    {
        float dx = MathF.Max(0f, MathF.Max(originX - camera.X, camera.X - (originX + Chunk.Size)));
        float dz = MathF.Max(0f, MathF.Max(originZ - camera.Z, camera.Z - (originZ + Chunk.Size)));
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// How far from <paramref name="origin"/> the nearest chunk still showing the previous
    /// presentation begins, in metres over the ground, or infinity once everything has switched.
    /// The mode-switch wave never runs ahead of this, so everything inside its ring is guaranteed new.
    /// </summary>
    public float UnconvertedRadius(Vector3 origin)
    {
        float nearest = float.PositiveInfinity;

        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            if (IsConverted(coord, entry)) continue;

            float distance = MathF.Sqrt(DistanceSquaredToColumn(origin, coord.X * Chunk.Size, coord.Z * Chunk.Size));
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }

    /// <summary>Chunks whose meshes still show the previous presentation</summary>
    public int UnconvertedChunks
    {
        get
        {
            int count = 0;
            foreach ((ChunkCoord coord, Entry entry) in _entries)
                if (!IsConverted(coord, entry)) count++;
            return count;
        }
    }

    // A chunk that cannot be meshed right now (it lost a neighbour at the far ring) keeps its old
    // look and is not waited for; it sits out of sight and is rebuilt once the gap fills
    private bool IsConverted(ChunkCoord coord, Entry entry)
        => !entry.HasMeshes || entry.SmoothMask == (_smooth ? entry.MeshedMask : 0) || !_world.IsMeshable(coord);

    /// <summary>One line about a chunk's meshes, for the smoke report and bug hunts</summary>
    public string Describe(ChunkCoord coord)
    {
        if (!_entries.TryGetValue(coord, out Entry? entry)) return $"{coord}: no entry (dirty={_scheduler.IsDirty(coord)}, inFlight={_scheduler.InFlight(coord)})";

        var parts = new System.Text.StringBuilder();
        for (int section = 0; section < SectionCount; section++)
        {
            GpuMesh[]? list = entry.Sections[section];
            parts.Append(list == null ? " -" : $" {list.Length}p/{list.Sum(p => p.TriangleCount)}t");
        }

        return $"{coord}: lod={entry.Lod} meshed={Convert.ToString(entry.MeshedMask, 2)} smooth={Convert.ToString(entry.SmoothMask, 2)} sections:{parts} dirty={_scheduler.IsDirty(coord)} inFlight={_scheduler.InFlight(coord)}";
    }

    public void DrawChunkBounds()
    {
        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            var center = new Vector3(
                coord.X * Chunk.Size + Chunk.Size / 2f,
                VoxelWorld.WorldHeight / 2f,
                coord.Z * Chunk.Size + Chunk.Size / 2f);

            Color color = entry.Lod switch { 0 => Color.Magenta, 1 => Color.Orange, _ => Color.SkyBlue };
            Raylib.DrawCubeWires(center, Chunk.Size, VoxelWorld.WorldHeight, Chunk.Size, color);
        }
    }

    public void Dispose()
    {
        // Queued jobs are skipped rather than finished: quitting or switching games must not
        // wait for a backlog of meshes nobody will see
        _disposing = true;
        _jobs.CompleteAdding();
        foreach (Thread worker in _workers)
            worker.Join();

        // Results carry no pooled memory: a job returns its grid to the pool before it reports
        while (_results.TryDequeue(out _)) { }

        foreach (Entry entry in _entries.Values)
            FreeAll(entry);

        _entries.Clear();
    }
}
