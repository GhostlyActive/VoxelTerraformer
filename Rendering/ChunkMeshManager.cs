using Raylib_cs;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using Terraformer.World;

namespace Terraformer.Rendering;

/// <summary>
/// Hält pro Chunk ein GPU-Mesh und baut es bei Änderungen im Hintergrund neu.
/// Snapshot und Upload laufen auf dem Main-Thread (GL-Kontext), das eigentliche Meshing
/// auf einem Worker-Thread — das alte Mesh bleibt sichtbar, bis das neue fertig ist.
/// </summary>
public sealed class ChunkMeshManager : IDisposable
{
    private sealed class Entry
    {
        public Mesh Mesh;
        public bool HasMesh;
    }

    private readonly record struct MeshJob(ChunkCoord Coord, byte[] Padded, int WorldX, int WorldZ);
    private readonly record struct MeshResult(ChunkCoord Coord, ChunkMeshData Data);

    private readonly VoxelWorld _world;
    private readonly Dictionary<ChunkCoord, Entry> _entries = new();
    private readonly HashSet<ChunkCoord> _dirty = new();
    private readonly HashSet<ChunkCoord> _inFlight = new();
    private readonly List<ChunkCoord> _startable = new();

    private readonly BlockingCollection<MeshJob> _jobs = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    private readonly Thread _worker;

    public int VisibleChunks { get; private set; }
    public int MeshedChunks => _entries.Count;
    public int PendingChunks => _dirty.Count + _inFlight.Count;
    public int TotalVertices { get; private set; }

    public ChunkMeshManager(VoxelWorld world)
    {
        _world = world;
        _world.ChunkDirty += coord => _dirty.Add(coord);
        _world.ChunkUnloaded += OnChunkUnloaded;

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ChunkMesher" };
        _worker.Start();
    }

    private void OnChunkUnloaded(ChunkCoord coord)
    {
        _dirty.Remove(coord);
        // Ein evtl. laufender Job wird beim Eintreffen des Ergebnisses verworfen (Chunk existiert nicht mehr)

        if (_entries.Remove(coord, out Entry? entry) && entry.HasMesh)
            Raylib.UnloadMesh(entry.Mesh);
    }

    /// <summary>Meshed alle vorhandenen Chunks synchron — einmalig beim Start, damit die Welt komplett dasteht</summary>
    public void BuildAllNow()
    {
        foreach (Chunk chunk in _world.Chunks)
        {
            byte[] padded = RentSnapshot(chunk);
            ChunkMeshData data = ChunkMesher.Build(
                padded, VoxelWorld.WorldHeight, (int)chunk.WorldPosition.X, (int)chunk.WorldPosition.Z);
            ArrayPool<byte>.Shared.Return(padded);
            Upload(chunk.Coord, data);
        }
    }

    public void Update()
    {
        // Fertige Meshes hochladen — GL-Kontext, deshalb nur hier auf dem Main-Thread
        while (_results.TryDequeue(out MeshResult result))
        {
            _inFlight.Remove(result.Coord);
            if (!_world.TryGetChunk(result.Coord, out _)) continue; // inzwischen entladen
            Upload(result.Coord, result.Data);
        }

        if (_dirty.Count == 0) return;

        // Pro Chunk maximal ein Job unterwegs; erneut dirty gewordene bleiben bis zur nächsten Runde in der Menge
        _startable.Clear();
        foreach (ChunkCoord coord in _dirty)
            if (!_inFlight.Contains(coord))
                _startable.Add(coord);

        foreach (ChunkCoord coord in _startable)
        {
            _dirty.Remove(coord);
            if (!_world.TryGetChunk(coord, out Chunk chunk)) continue;

            _inFlight.Add(coord);
            _jobs.Add(new MeshJob(
                coord, RentSnapshot(chunk), (int)chunk.WorldPosition.X, (int)chunk.WorldPosition.Z));
        }
    }

    private void WorkerLoop()
    {
        foreach (MeshJob job in _jobs.GetConsumingEnumerable())
        {
            ChunkMeshData data = ChunkMesher.Build(job.Padded, VoxelWorld.WorldHeight, job.WorldX, job.WorldZ);
            ArrayPool<byte>.Shared.Return(job.Padded);
            _results.Enqueue(new MeshResult(job.Coord, data));
        }
    }

    private byte[] RentSnapshot(Chunk chunk)
    {
        const int worldHeight = VoxelWorld.WorldHeight;
        byte[] padded = ArrayPool<byte>.Shared.Rent(ChunkMesher.PaddedLength(worldHeight));

        int baseX = (int)chunk.WorldPosition.X;
        int baseZ = (int)chunk.WorldPosition.Z;

        // Innenbereich zeilenweise am Stück kopieren
        for (int y = 0; y < worldHeight; y++)
        for (int z = 0; z < Chunk.Size; z++)
            chunk.CopyRow(y, z, padded, ChunkMesher.Index(0, y, z));

        // 1 Voxel dicke Schale aus der Welt (Nachbar-Chunks) — für Face-Culling und AO an den Grenzen
        for (int y = -1; y <= worldHeight; y++)
        for (int z = -1; z <= Chunk.Size; z++)
        for (int x = -1; x <= Chunk.Size; x++)
        {
            bool inside = x >= 0 && x < Chunk.Size && z >= 0 && z < Chunk.Size && y >= 0 && y < worldHeight;
            if (inside) continue;

            // Unterhalb der Welt gilt als solide, damit die nie sichtbare Weltunterseite kein Mesh erzeugt
            padded[ChunkMesher.Index(x, y, z)] = y < 0
                ? BlockRegistry.Terrain
                : (byte)_world.GetBlock(baseX + x, y, baseZ + z);
        }

        return padded;
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
        _worker.Join();

        // Übrige Ergebnisse verwerfen — die zugehörigen Meshes werden unten freigegeben
        while (_results.TryDequeue(out _)) { }

        foreach (Entry entry in _entries.Values)
            if (entry.HasMesh)
                Raylib.UnloadMesh(entry.Mesh);
        _entries.Clear();
    }
}
