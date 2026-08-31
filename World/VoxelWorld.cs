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

    // Ziel im Fadenkreuz: entweder ein getroffener Block (Hover) oder — wenn innerhalb
    // der Reichweite nichts im Weg ist — eine freie Zelle in der Luft (Ghost)
    private bool _hasHover;
    private VoxelRaycast.Vector3Int _hoverBlock;
    private VoxelRaycast.Vector3Int _hoverPlaceCell;
    private Vector3 _hoverCenter;

    private bool _hasGhost;
    private VoxelRaycast.Vector3Int _ghostCell;
    private Vector3 _ghostCenter;

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

    public void Update(Camera3D camera, BoundingBox playerBounds, float buildReach)
    {
        // Inputs: Mouse + keyboard fallback
        bool remove = Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.O);
        bool place  = Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.P);

        // Hover/Ghost jeden Frame aktualisieren — Abbauen/Bauen wirken exakt auf das markierte Ziel
        UpdateHover(camera, buildReach);

        if (remove) TryRemove();
        if (place)  TryPlace(playerBounds);
    }

    public void DrawHover()
    {
        if (_hasHover)
            Raylib.DrawCubeWires(_hoverCenter, 1.02f, 1.02f, 1.02f, Color.Yellow);
        else if (_hasGhost)
            Raylib.DrawCubeWires(_ghostCenter, 1f, 1f, 1f, new Color(95, 225, 235, 220));
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

        // Nichts im Weg → Bau-Ziel frei in der Luft am Ende der Reichweite
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
        // Blick auf einen Block → an dessen Fläche bauen; sonst frei in die Luft auf Reichweite
        VoxelRaycast.Vector3Int cell;
        if (_hasHover) cell = _hoverPlaceCell;
        else if (_hasGhost) cell = _ghostCell;
        else return;

        if (GetBlock(cell.X, cell.Y, cell.Z) != 0) return; // must be air
        if (IntersectsBlock(playerBounds, cell.X, cell.Y, cell.Z)) return; // nicht in den Spieler hinein bauen

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
