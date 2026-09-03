using Raylib_cs;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Keeps the GPU meshes of the loaded chunks and rebuilds them in the background when something
/// changes. Snapshot and upload run on the main thread (GL context), the meshing itself on a worker
/// thread; the old mesh stays visible until the new one is ready.
///
/// A chunk is not one mesh but a stack of <see cref="SectionCount"/> sections. An edit only dirties
/// the sections it touched, which matters as soon as terrain has caves and overhangs: meshing the
/// whole column for a one-metre brush stroke costs the same as meshing it for a bomb crater.
/// It also lets the frustum throw away sections rather than whole columns.
/// </summary>
public sealed class ChunkMeshManager : IDisposable
{
    /// <summary>Blocks per meshing section; <see cref="VoxelWorld.WorldHeight"/> must divide by it</summary>
    public const int SectionHeight = 16;

    public const int SectionCount = VoxelWorld.WorldHeight / SectionHeight;

    private readonly record struct SectionKey(ChunkCoord Coord, int Section);

    private sealed class Entry
    {
        public readonly Mesh[] Meshes = new Mesh[SectionCount];
        public readonly bool[] HasMesh = new bool[SectionCount];
    }

    // One job per chunk, carrying a bit per section that needs rebuilding. Sections used to be
    // jobs of their own, which meant taking the same snapshot four times over for the same chunk —
    // and the snapshot is the part that runs on the main thread.
    private readonly record struct MeshJob(
        ChunkCoord Coord, int Sections, byte[] Padded, Dictionary<int, byte[]> Refinements,
        int WorldX, int WorldZ, int Generation, bool Smooth);

    private readonly record struct MeshResult(
        ChunkCoord Coord, int Sections, ChunkMeshData?[] Data, int Generation, bool Smooth);

    private readonly VoxelWorld _world;
    private readonly Dictionary<ChunkCoord, Entry> _entries = new();
    private readonly HashSet<SectionKey> _dirty = new();
    private readonly HashSet<ChunkCoord> _inFlight = new();
    private readonly Dictionary<ChunkCoord, int> _startable = new();

    // Counts unloads per coordinate: results from older generations (from before a "Load world",
    // for instance) are discarded even when the coordinate is occupied again
    private readonly Dictionary<ChunkCoord, int> _generations = new();

    private readonly BlockingCollection<MeshJob> _jobs = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    private readonly Thread[] _workers;

    /// <summary>Marching-cubes presentation instead of hard-edged cubes (Smooth mode)</summary>
    public bool SmoothRendering { get; private set; }

    public int VisibleChunks { get; private set; }
    public int MeshedChunks => _entries.Count;
    public int PendingChunks => _dirty.Count + _inFlight.Count;
    public int TotalVertices { get; private set; }

    public ChunkMeshManager(VoxelWorld world)
    {
        _world = world;
        _world.ChunkDirty += MarkDirty;
        _world.ChunkUnloaded += OnChunkUnloaded;

        // Several workers, because marching-cubes meshing is far more expensive than the blocky
        // kind. Only one job per section is ever in flight, so an older mesh can never overtake a newer one.
        int workerCount = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"ChunkMesher{i}" };
            _workers[i].Start();
        }
    }

    /// <summary>An edit between <paramref name="minY"/> and <paramref name="maxY"/> dirties the sections it spans</summary>
    private void MarkDirty(ChunkCoord coord, int minY, int maxY)
    {
        // Widened by one block: a section's outermost blocks read the neighbourhood beyond it, so
        // an edit right at a section border changes the mesh on both sides
        int first = Math.Clamp((minY - 1) / SectionHeight, 0, SectionCount - 1);
        int last = Math.Clamp((maxY + 1) / SectionHeight, 0, SectionCount - 1);

        for (int section = first; section <= last; section++)
            _dirty.Add(new SectionKey(coord, section));
    }

    /// <summary>Switch the presentation; every loaded chunk is remeshed</summary>
    public void SetSmoothRendering(bool smooth)
    {
        if (smooth == SmoothRendering) return;

        SmoothRendering = smooth;
        foreach (Chunk chunk in _world.Chunks)
            MarkDirty(chunk.Coord, 0, VoxelWorld.WorldHeight - 1);
    }

    private void OnChunkUnloaded(ChunkCoord coord)
    {
        for (int section = 0; section < SectionCount; section++)
            _dirty.Remove(new SectionKey(coord, section));

        _inFlight.Remove(coord);

        _generations[coord] = GenerationOf(coord) + 1; // invalidate running jobs for this coordinate

        if (!_entries.Remove(coord, out Entry? entry)) return;

        for (int section = 0; section < SectionCount; section++)
            if (entry.HasMesh[section])
                Raylib.UnloadMesh(entry.Meshes[section]);
    }

    private int GenerationOf(ChunkCoord coord)
        => _generations.TryGetValue(coord, out int generation) ? generation : 0;

    /// <summary>Meshes every existing chunk synchronously; done once at startup so the world stands complete</summary>
    public void BuildAllNow()
    {
        foreach (Chunk chunk in _world.Chunks)
        {
            byte[] padded = RentSnapshot(chunk);
            Dictionary<int, byte[]> refinements = SnapshotRefinements(chunk);
            int worldX = (int)chunk.WorldPosition.X;
            int worldZ = (int)chunk.WorldPosition.Z;

            for (int section = 0; section < SectionCount; section++)
            {
                ChunkMeshData data = BuildSection(padded, refinements, worldX, worldZ, section, SmoothRendering);
                Upload(new SectionKey(chunk.Coord, section), data);
            }

            ArrayPool<byte>.Shared.Return(padded);
        }
    }

    private static ChunkMeshData BuildSection(
        byte[] padded, Dictionary<int, byte[]> refinements, int worldX, int worldZ, int section, bool smooth)
    {
        int start = section * SectionHeight;
        int end = start + SectionHeight;

        return smooth
            ? SmoothChunkMesher.Build(padded, refinements, VoxelWorld.WorldHeight, worldX, worldZ, start, end)
            : ChunkMesher.Build(padded, refinements, VoxelWorld.WorldHeight, worldX, worldZ, start, end);
    }

    private Entry EntryFor(ChunkCoord coord)
    {
        if (_entries.TryGetValue(coord, out Entry? entry)) return entry;

        entry = new Entry();
        _entries[coord] = entry;

        return entry;
    }

    public void Update()
    {
        // Upload finished meshes: GL context, so only here on the main thread
        while (_results.TryDequeue(out MeshResult result))
        {
            _inFlight.Remove(result.Coord);

            if (result.Generation != GenerationOf(result.Coord)) continue; // snapshot of a discarded state
            if (result.Smooth != SmoothRendering) continue; // presentation switched in the meantime
            if (!_world.TryGetChunk(result.Coord, out _)) continue; // unloaded in the meantime

            for (int section = 0; section < SectionCount; section++)
                if (result.Data[section] is { } data)
                    Upload(new SectionKey(result.Coord, section), data);
        }

        if (_dirty.Count == 0) return;

        // Gather the dirty sections per chunk; a chunk already being meshed waits for the next round
        _startable.Clear();
        foreach (SectionKey key in _dirty)
        {
            if (_inFlight.Contains(key.Coord)) continue;

            _startable.TryGetValue(key.Coord, out int mask);
            _startable[key.Coord] = mask | (1 << key.Section);
        }

        // Snapshots run on the main thread, throttled so a mode switch (everything dirty at once)
        // does not stutter the frame
        const int maxChunksPerFrame = 8;
        int started = 0;

        foreach ((ChunkCoord coord, int sections) in _startable)
        {
            if (started >= maxChunksPerFrame) break;

            for (int section = 0; section < SectionCount; section++)
                if ((sections & (1 << section)) != 0)
                    _dirty.Remove(new SectionKey(coord, section));

            if (!_world.TryGetChunk(coord, out Chunk chunk)) continue;

            started++;

            _inFlight.Add(coord);
            _jobs.Add(new MeshJob(
                coord, sections, RentSnapshot(chunk), SnapshotRefinements(chunk),
                (int)chunk.WorldPosition.X, (int)chunk.WorldPosition.Z, GenerationOf(coord), SmoothRendering));
        }
    }

    private void WorkerLoop()
    {
        foreach (MeshJob job in _jobs.GetConsumingEnumerable())
        {
            var data = new ChunkMeshData?[SectionCount];

            for (int section = 0; section < SectionCount; section++)
                if ((job.Sections & (1 << section)) != 0)
                    data[section] = BuildSection(
                        job.Padded, job.Refinements, job.WorldX, job.WorldZ, section, job.Smooth);

            ArrayPool<byte>.Shared.Return(job.Padded);
            _results.Enqueue(new MeshResult(job.Coord, job.Sections, data, job.Generation, job.Smooth));
        }
    }

    private byte[] RentSnapshot(Chunk chunk)
    {
        const int worldHeight = VoxelWorld.WorldHeight;
        byte[] padded = ArrayPool<byte>.Shared.Rent(ChunkMesher.PaddedLength(worldHeight));

        int baseX = (int)chunk.WorldPosition.X;
        int baseZ = (int)chunk.WorldPosition.Z;

        // Copy the interior row by row in one go
        for (int y = 0; y < worldHeight; y++)
        for (int z = 0; z < Chunk.Size; z++)
            chunk.CopyRow(y, z, padded, ChunkMesher.Index(0, y, z));

        // A one-voxel shell from the world (neighbouring chunks), for face culling and AO at the
        // borders. Only the shell cells are visited: walking the full padded volume and skipping
        // the interior costs seven times as much, and this runs on the main thread for every job.
        for (int y = -1; y <= worldHeight; y++)
        {
            bool yShell = y < 0 || y >= worldHeight;

            for (int z = -1; z <= Chunk.Size; z++)
            {
                if (yShell || z < 0 || z >= Chunk.Size)
                {
                    for (int x = -1; x <= Chunk.Size; x++)
                        padded[ChunkMesher.Index(x, y, z)] = ShellBlock(baseX + x, y, baseZ + z);

                    continue;
                }

                padded[ChunkMesher.Index(-1, y, z)] = ShellBlock(baseX - 1, y, baseZ + z);
                padded[ChunkMesher.Index(Chunk.Size, y, z)] = ShellBlock(baseX + Chunk.Size, y, baseZ + z);
            }
        }

        return padded;
    }

    /// <summary>Below the world counts as solid, so the never-visible underside produces no mesh</summary>
    private byte ShellBlock(int worldX, int worldY, int worldZ)
        => worldY < 0 ? BlockRegistry.Terrain : (byte)_world.GetBlock(worldX, worldY, worldZ);

    // The chunk's sub-voxel density fields (rekeyed to padded indices) plus those of the directly
    // adjacent neighbour blocks, for sub-voxel culling at the chunk borders
    private Dictionary<int, byte[]> SnapshotRefinements(Chunk chunk)
    {
        var refinements = new Dictionary<int, byte[]>();

        foreach ((int index, byte[] field) in chunk.Refinements)
        {
            (int x, int y, int z) = Chunk.DecodeIndex(index);
            refinements[ChunkMesher.Index(x, y, z)] = field;
        }

        int baseX = (int)chunk.WorldPosition.X;
        int baseZ = (int)chunk.WorldPosition.Z;

        for (int y = 0; y < VoxelWorld.WorldHeight; y++)
        for (int i = 0; i < Chunk.Size; i++)
        {
            AddBorderRefinement(refinements, baseX - 1, y, baseZ + i, -1, y, i);
            AddBorderRefinement(refinements, baseX + Chunk.Size, y, baseZ + i, Chunk.Size, y, i);
            AddBorderRefinement(refinements, baseX + i, y, baseZ - 1, i, y, -1);
            AddBorderRefinement(refinements, baseX + i, y, baseZ + Chunk.Size, i, y, Chunk.Size);
        }

        // Diagonal corner columns: the smooth mesher reads the full 3x3x3 neighbourhood
        for (int y = 0; y < VoxelWorld.WorldHeight; y++)
        {
            AddBorderRefinement(refinements, baseX - 1, y, baseZ - 1, -1, y, -1);
            AddBorderRefinement(refinements, baseX - 1, y, baseZ + Chunk.Size, -1, y, Chunk.Size);
            AddBorderRefinement(refinements, baseX + Chunk.Size, y, baseZ - 1, Chunk.Size, y, -1);
            AddBorderRefinement(refinements, baseX + Chunk.Size, y, baseZ + Chunk.Size, Chunk.Size, y, Chunk.Size);
        }

        return refinements;
    }

    private void AddBorderRefinement(Dictionary<int, byte[]> refinements, int wx, int wy, int wz, int lx, int ly, int lz)
    {
        if (_world.TryGetRefinement(wx, wy, wz, out byte[] field))
            refinements[ChunkMesher.Index(lx, ly, lz)] = field;
    }

    private void Upload(SectionKey key, ChunkMeshData data)
    {
        Entry entry = EntryFor(key.Coord);

        if (entry.HasMesh[key.Section])
        {
            Raylib.UnloadMesh(entry.Meshes[key.Section]);
            entry.HasMesh[key.Section] = false;
        }

        if (data.VertexCount == 0) return;

        var mesh = new Mesh(data.VertexCount, data.VertexCount / 3);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocColors();
        data.Vertices.AsSpan(0, data.VertexCount * 3).CopyTo(mesh.VerticesAs<float>());
        data.Normals.AsSpan(0, data.VertexCount * 3).CopyTo(mesh.NormalsAs<float>());
        data.Colors.AsSpan(0, data.VertexCount * 4).CopyTo(mesh.ColorsAs<byte>());
        Raylib.UploadMesh(ref mesh, false);

        entry.Meshes[key.Section] = mesh;
        entry.HasMesh[key.Section] = true;
    }

    public void Draw(Material material, Frustum frustum)
    {
        VisibleChunks = 0;
        TotalVertices = 0;

        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            // Vertices are in chunk-local coordinates, so one translation carries every section
            Matrix4x4 transform = Raymath.MatrixTranslate(coord.X * Chunk.Size, 0, coord.Z * Chunk.Size);

            for (int section = 0; section < SectionCount; section++)
            {
                if (!entry.HasMesh[section]) continue;
                TotalVertices += entry.Meshes[section].VertexCount;

                var min = new Vector3(coord.X * Chunk.Size, section * SectionHeight, coord.Z * Chunk.Size);
                var max = min + new Vector3(Chunk.Size, SectionHeight, Chunk.Size);
                if (!frustum.Intersects(min, max)) continue;

                VisibleChunks++;
                Raylib.DrawMesh(entry.Meshes[section], material, transform);
            }
        }
    }

    public void DrawChunkBounds()
    {
        foreach ((ChunkCoord coord, Entry _) in _entries)
        {
            var center = new Vector3(
                coord.X * Chunk.Size + Chunk.Size / 2f,
                VoxelWorld.WorldHeight / 2f,
                coord.Z * Chunk.Size + Chunk.Size / 2f);
            Raylib.DrawCubeWires(center, Chunk.Size, VoxelWorld.WorldHeight, Chunk.Size, Color.Magenta);
        }
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        foreach (Thread worker in _workers)
            worker.Join();

        // Discard the remaining results; their meshes are released below
        while (_results.TryDequeue(out _)) { }

        foreach (Entry entry in _entries.Values)
            for (int section = 0; section < SectionCount; section++)
                if (entry.HasMesh[section])
                    Raylib.UnloadMesh(entry.Meshes[section]);

        _entries.Clear();
    }
}
