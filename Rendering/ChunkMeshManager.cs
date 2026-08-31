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

    private readonly record struct MeshJob(ChunkCoord Coord, byte[] Padded, Dictionary<int, ulong[]> Refinements, int WorldX, int WorldZ, int Generation, bool Smooth);
    private readonly record struct MeshResult(ChunkCoord Coord, ChunkMeshData Data, int Generation, bool Smooth);

    private readonly VoxelWorld _world;
    private readonly Dictionary<ChunkCoord, Entry> _entries = new();
    private readonly HashSet<ChunkCoord> _dirty = new();
    private readonly HashSet<ChunkCoord> _inFlight = new();
    private readonly List<ChunkCoord> _startable = new();

    // Zählt pro Koordinate die Entladungen: Ergebnisse alter Generationen (z. B. von vor
    // einem "Load world") werden verworfen, auch wenn die Koordinate wieder belegt ist
    private readonly Dictionary<ChunkCoord, int> _generations = new();

    private readonly BlockingCollection<MeshJob> _jobs = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    private readonly Thread[] _workers;

    /// <summary>Marching-Cubes-Darstellung statt kantiger Würfel (Smooth-Modus)</summary>
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

        // Mehrere Worker, weil das Marching-Cubes-Meshing deutlich teurer ist als das kantige.
        // Pro Chunk ist immer nur ein Job unterwegs, deshalb kann kein älteres Mesh ein neueres überholen.
        int workerCount = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"ChunkMesher{i}" };
            _workers[i].Start();
        }
    }

    /// <summary>Darstellung umschalten — alle geladenen Chunks werden neu gemesht</summary>
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
        _generations[coord] = GenerationOf(coord) + 1; // laufende Jobs dieser Koordinate entwerten

        if (_entries.Remove(coord, out Entry? entry) && entry.HasMesh)
            Raylib.UnloadMesh(entry.Mesh);
    }

    private int GenerationOf(ChunkCoord coord)
        => _generations.TryGetValue(coord, out int generation) ? generation : 0;

    /// <summary>Meshed alle vorhandenen Chunks synchron — einmalig beim Start, damit die Welt komplett dasteht</summary>
    public void BuildAllNow()
    {
        foreach (Chunk chunk in _world.Chunks)
        {
            byte[] padded = RentSnapshot(chunk);
            Dictionary<int, ulong[]> refinements = SnapshotRefinements(chunk);
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
        // Fertige Meshes hochladen — GL-Kontext, deshalb nur hier auf dem Main-Thread
        while (_results.TryDequeue(out MeshResult result))
        {
            _inFlight.Remove(result.Coord);
            if (result.Generation != GenerationOf(result.Coord)) continue; // Snapshot eines verworfenen Zustands
            if (result.Smooth != SmoothRendering) continue; // Darstellung inzwischen umgeschaltet
            if (!_world.TryGetChunk(result.Coord, out _)) continue; // inzwischen entladen
            Upload(result.Coord, result.Data);
        }

        if (_dirty.Count == 0) return;

        // Pro Chunk maximal ein Job unterwegs; erneut dirty gewordene bleiben bis zur nächsten Runde in der Menge
        _startable.Clear();
        foreach (ChunkCoord coord in _dirty)
            if (!_inFlight.Contains(coord))
                _startable.Add(coord);

        // Snapshots laufen auf dem Main-Thread — gedrosselt, damit ein Moduswechsel
        // (alle Chunks auf einmal dirty) keinen Frame-Ruckler erzeugt
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

    // Sub-Voxel-Masken des Chunks (auf Padded-Indizes umgeschlüsselt) plus die der
    // direkt angrenzenden Nachbarblöcke — fürs Sub-Culling an den Chunk-Grenzen
    private Dictionary<int, ulong[]> SnapshotRefinements(Chunk chunk)
    {
        var refinements = new Dictionary<int, ulong[]>();

        foreach ((int index, ulong[] mask) in chunk.Refinements)
        {
            (int x, int y, int z) = Chunk.DecodeIndex(index);
            refinements[ChunkMesher.Index(x, y, z)] = mask;
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

        // Diagonale Eckspalten — der Smooth-Mesher liest die volle 3x3x3-Nachbarschaft
        for (int y = 0; y < VoxelWorld.WorldHeight; y++)
        {
            AddBorderRefinement(refinements, baseX - 1, y, baseZ - 1, -1, y, -1);
            AddBorderRefinement(refinements, baseX - 1, y, baseZ + Chunk.Size, -1, y, Chunk.Size);
            AddBorderRefinement(refinements, baseX + Chunk.Size, y, baseZ - 1, Chunk.Size, y, -1);
            AddBorderRefinement(refinements, baseX + Chunk.Size, y, baseZ + Chunk.Size, Chunk.Size, y, Chunk.Size);
        }

        return refinements;
    }

    private void AddBorderRefinement(Dictionary<int, ulong[]> refinements, int wx, int wy, int wz, int lx, int ly, int lz)
    {
        if (_world.TryGetRefinement(wx, wy, wz, out ulong[] mask))
            refinements[ChunkMesher.Index(lx, ly, lz)] = mask;
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

        // Übrige Ergebnisse verwerfen — die zugehörigen Meshes werden unten freigegeben
        while (_results.TryDequeue(out _)) { }

        foreach (Entry entry in _entries.Values)
            if (entry.HasMesh)
                Raylib.UnloadMesh(entry.Mesh);
        _entries.Clear();
    }
}
