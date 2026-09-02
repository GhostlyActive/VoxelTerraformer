using Raylib_cs;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Keeps one GPU mesh per chunk and rebuilds it in the background when something changes.
/// Snapshot and upload run on the main thread (GL context), the meshing itself on a worker thread;
/// the old mesh stays visible until the new one is ready.
/// </summary>
public sealed class ChunkMeshManager : IDisposable
{
    private sealed class Entry
    {
        public Mesh Mesh;
        public bool HasMesh;
    }

    private readonly record struct MeshJob(ChunkCoord Coord, byte[] Padded, Dictionary<int, byte[]> Refinements, int WorldX, int WorldZ, int Generation, bool Smooth);
    private readonly record struct MeshResult(ChunkCoord Coord, ChunkMeshData Data, int Generation, bool Smooth);

    private readonly VoxelWorld _world;
    private readonly Dictionary<ChunkCoord, Entry> _entries = new();
    private readonly HashSet<ChunkCoord> _dirty = new();
    private readonly HashSet<ChunkCoord> _inFlight = new();
    private readonly List<ChunkCoord> _startable = new();

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
        _world.ChunkDirty += coord => _dirty.Add(coord);
        _world.ChunkUnloaded += OnChunkUnloaded;

        // Several workers, because marching-cubes meshing is far more expensive than the blocky
        // kind. Only one job per chunk is ever in flight, so an older mesh can never overtake a newer one.
        int workerCount = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"ChunkMesher{i}" };
            _workers[i].Start();
        }
    }

    /// <summary>Switch the presentation; every loaded chunk is remeshed</summary>
    public void SetSmoothRendering(bool smooth)
    {
        if (smooth == SmoothRendering) return;

        SmoothRendering = smooth;
        foreach (Chunk chunk in _world.Chunks)
            _dirty.Add(chunk.Coord);
    }

    private void OnChunkUnloaded(ChunkCoord coord)
    {
        _dirty.Remove(coord);
        _generations[coord] = GenerationOf(coord) + 1; // invalidate running jobs for this coordinate

        if (_entries.Remove(coord, out Entry? entry) && entry.HasMesh)
            Raylib.UnloadMesh(entry.Mesh);
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

            ChunkMeshData data = SmoothRendering
                ? SmoothChunkMesher.Build(padded, refinements, VoxelWorld.WorldHeight, worldX, worldZ)
                : ChunkMesher.Build(padded, refinements, VoxelWorld.WorldHeight, worldX, worldZ);

            ArrayPool<byte>.Shared.Return(padded);
            Upload(chunk.Coord, data);
        }
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
            Upload(result.Coord, result.Data);
        }

        if (_dirty.Count == 0) return;

        // At most one job per chunk; anything dirtied again stays in the set until the next round
        _startable.Clear();
        foreach (ChunkCoord coord in _dirty)
            if (!_inFlight.Contains(coord))
                _startable.Add(coord);

        // Snapshots run on the main thread, throttled so a mode switch (every chunk dirty at once)
        // does not stutter the frame
        const int maxStartsPerFrame = 12;
        int started = 0;

        foreach (ChunkCoord coord in _startable)
        {
            if (started >= maxStartsPerFrame) break;

            _dirty.Remove(coord);
            if (!_world.TryGetChunk(coord, out Chunk chunk)) continue;

            started++;

            _inFlight.Add(coord);
            _jobs.Add(new MeshJob(
                coord, RentSnapshot(chunk), SnapshotRefinements(chunk),
                (int)chunk.WorldPosition.X, (int)chunk.WorldPosition.Z, GenerationOf(coord), SmoothRendering));
        }
    }

    private void WorkerLoop()
    {
        foreach (MeshJob job in _jobs.GetConsumingEnumerable())
        {
            ChunkMeshData data = job.Smooth
                ? SmoothChunkMesher.Build(job.Padded, job.Refinements, VoxelWorld.WorldHeight, job.WorldX, job.WorldZ)
                : ChunkMesher.Build(job.Padded, job.Refinements, VoxelWorld.WorldHeight, job.WorldX, job.WorldZ);

            ArrayPool<byte>.Shared.Return(job.Padded);
            _results.Enqueue(new MeshResult(job.Coord, data, job.Generation, job.Smooth));
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

    private void Upload(ChunkCoord coord, ChunkMeshData data)
    {
        if (!_entries.TryGetValue(coord, out Entry? entry))
        {
            entry = new Entry();
            _entries[coord] = entry;
        }

        if (entry.HasMesh)
        {
            Raylib.UnloadMesh(entry.Mesh);
            entry.HasMesh = false;
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

        entry.Mesh = mesh;
        entry.HasMesh = true;
    }

    public void Draw(Material material, Frustum frustum)
    {
        VisibleChunks = 0;
        TotalVertices = 0;

        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            if (!entry.HasMesh) continue;
            TotalVertices += entry.Mesh.VertexCount;

            var min = new Vector3(coord.X * Chunk.Size, 0, coord.Z * Chunk.Size);
            var max = min + new Vector3(Chunk.Size, VoxelWorld.WorldHeight, Chunk.Size);
            if (!frustum.Intersects(min, max)) continue;

            VisibleChunks++;
            Raylib.DrawMesh(entry.Mesh, material, Raymath.MatrixTranslate(min.X, min.Y, min.Z));
        }
    }

    public void DrawChunkBounds()
    {
        foreach ((ChunkCoord coord, Entry entry) in _entries)
        {
            if (!entry.HasMesh) continue;

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
            if (entry.HasMesh)
                Raylib.UnloadMesh(entry.Mesh);
        _entries.Clear();
    }
}
