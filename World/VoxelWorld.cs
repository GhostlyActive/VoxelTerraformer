using Raylib_cs;
using System.Numerics;
using Terraformer.MathTools;
using Terraformer.Rendering;

namespace Terraformer.World;

public class VoxelWorld
{
    public const int WorldHeight = 64;
    public const int StartChunksX = 8;
    public const int StartChunksZ = 8;

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();

    // Optional debug info (for highlighting)
    private bool _hasHover;
    private Vector3 _hoverCenter;

    /// <summary>Chunk braucht ein neues Mesh (feuert bei Kanten-Edits auch für Nachbarn)</summary>
    public event Action<ChunkCoord>? ChunkDirty;

    /// <summary>Block abgebaut: Zentrum + Albedo (z. B. für Partikel)</summary>
    public event Action<Vector3, Color>? BlockBroken;

    /// <summary>Block gesetzt: Zentrum + Albedo</summary>
    public event Action<Vector3, Color>? BlockPlaced;

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public VoxelWorld()
    {
        for (int cz = 0; cz < StartChunksZ; cz++)
        for (int cx = 0; cx < StartChunksX; cx++)
            GetOrCreateChunk(new ChunkCoord(cx, cz));
    }

    public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        => _chunks.TryGetValue(coord, out chunk!);

    public void Update(Camera3D camera, BoundingBox playerBounds)
    {
        // Inputs: Mouse + keyboard fallback
        bool remove = Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.O);
        bool place  = Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.P);

        // Always refresh hover (so you can see what you're aiming at)
        UpdateHover(camera);

        if (remove) TryRemove(camera);
        if (place)  TryPlace(camera, playerBounds);
    }

    public void DrawHover()
    {
        if (_hasHover)
            Raylib.DrawCubeWires(_hoverCenter, 1.02f, 1.02f, 1.02f, Color.Yellow);
    }

    // --- Interaction ---

    private void UpdateHover(Camera3D camera)
    {
        _hasHover = false;

        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, 60f);
        if (!hit.HasHit) return;

        _hoverCenter = new Vector3(hit.Block.X + 0.5f, hit.Block.Y + 0.5f, hit.Block.Z + 0.5f);
        _hasHover = true;
    }

    private void TryRemove(Camera3D camera)
    {
        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, 60f);
        if (!hit.HasHit) return;

        int id = GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z);
        Color albedo = TerrainColors.ForBlock(id, hit.Block.X, hit.Block.Y, hit.Block.Z);

        SetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z, BlockRegistry.Air);

        BlockBroken?.Invoke(
            new Vector3(hit.Block.X + 0.5f, hit.Block.Y + 0.5f, hit.Block.Z + 0.5f),
            albedo);
    }

    private void TryPlace(Camera3D camera, BoundingBox playerBounds)
    {
        Ray ray = Raylib.GetScreenToWorldRay(
            new Vector2(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2),
            camera
        );

        var hit = VoxelRaycast.Cast(GetBlock, ray.Position, ray.Direction, 60f);
        if (!hit.HasHit) return;

        // Place into neighbor cell (the face you are pointing at)
        int px = hit.PlaceBlock.X;
        int py = hit.PlaceBlock.Y;
        int pz = hit.PlaceBlock.Z;

        if (GetBlock(px, py, pz) != 0) return; // must be air
        if (IntersectsBlock(playerBounds, px, py, pz)) return; // nicht in den Spieler hinein bauen

        SetBlock(px, py, pz, BlockRegistry.Stone);

        BlockPlaced?.Invoke(
            new Vector3(px + 0.5f, py + 0.5f, pz + 0.5f),
            TerrainColors.ForBlock(BlockRegistry.Stone, px, py, pz));
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

    public void SetBlock(int wx, int wy, int wz, int id)
    {
        if (wy < 0 || wy >= WorldHeight) return;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        var chunk = GetOrCreateChunk(cc);

        chunk.SetLocal(lx, wy, lz, id, WorldHeight);

        FireDirtyAround(cc, lx, lz);
    }

    // Edits an Kanten/Ecken betreffen auch die Meshes der (diagonalen) Nachbarn — wegen Face-Culling und AO
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

    private Chunk GetOrCreateChunk(ChunkCoord cc)
    {
        if (_chunks.TryGetValue(cc, out var existing))
            return existing;

        var created = new Chunk(cc, WorldHeight);
        _chunks.Add(cc, created);
        return created;
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
