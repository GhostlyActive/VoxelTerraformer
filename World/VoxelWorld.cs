using Raylib_cs;
using System.Numerics;
using Terraformer.MathTools;
using Terraformer.Rendering;

namespace Terraformer.World;

public class VoxelWorld
{
    public const int WorldHeight = 64;

    // Streaming: geladen wird im Kreis um den Spieler, entladen mit Hysterese
    // (LoadRadius * Chunkgröße = 256 Blöcke — liegt hinter dem Fog-Ende, Nachladen bleibt unsichtbar)
    public const int LoadRadius = 8;
    public const int UnloadRadius = 10;

    private static readonly (int X, int Z)[] _loadOrder = BuildLoadOrder();

    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();
    private readonly WorldStorage _storage;

    // Veränderte Chunks überleben das Entladen im Speicher — auf Platte kommt nur, was
    // der Spieler ausdrücklich speichert. Beim App-Start ist die Welt immer frisch.
    private readonly Dictionary<ChunkCoord, Chunk> _keptModified = new();
    private bool _diskIsBase; // erst nach Save/Load ist der Spielstand die Basis fürs Chunk-Laden

    // Ziel im Fadenkreuz: entweder ein getroffener Block (Hover) oder — wenn innerhalb
    // der Reichweite nichts im Weg ist — eine freie Zelle in der Luft (Ghost)
    private bool _hasHover;
    private VoxelRaycast.Vector3Int _hoverBlock;
    private VoxelRaycast.Vector3Int _hoverPlaceCell;
    private Vector3 _hoverCenter;

    private bool _hasGhost;
    private VoxelRaycast.Vector3Int _ghostCell;
    private Vector3 _ghostCenter;

    // Sculpt-Modus: Kugel-Brush auf Sub-Voxel-Ebene, halten = kontinuierlich bohren/auftragen
    private const float SculptInterval = 0.05f;
    private bool _hasSculptTarget;
    private Vector3 _sculptTarget;
    private float _sculptCooldown;
    private float _sculptRadiusForDraw;

    /// <summary>Umschaltbar per Taste V: Block-Modus vs. Feinverformung</summary>
    public bool SculptMode { get; set; }

    /// <summary>Chunk braucht ein neues Mesh (feuert bei Kanten-Edits auch für Nachbarn)</summary>
    public event Action<ChunkCoord>? ChunkDirty;

    /// <summary>Block abgebaut: Zentrum + Albedo (z. B. für Partikel)</summary>
    public event Action<Vector3, Color>? BlockBroken;

    /// <summary>Block gesetzt: Zentrum + Albedo</summary>
    public event Action<Vector3, Color>? BlockPlaced;

    /// <summary>Chunk wurde entladen — Mesh kann weg</summary>
    public event Action<ChunkCoord>? ChunkUnloaded;

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public int LoadedChunkCount => _chunks.Count;

    public VoxelWorld(WorldStorage storage)
    {
        _storage = storage;
    }

    public bool TryGetChunk(ChunkCoord coord, out Chunk chunk)
        => _chunks.TryGetValue(coord, out chunk!);

    // --- Streaming ---

    /// <summary>Lädt sofort alles im Radius (Blocking) — für den Spielstart um den Spawn</summary>
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

    /// <summary>Pro Frame aufrufen: lädt die nächsten fehlenden Chunks (Budget) und entlädt ferne</summary>
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

    /// <summary>Manuelles Speichern: der komplette aktuelle Weltzustand wird zum Spielstand</summary>
    public void SaveWorld()
    {
        // Frische Session: der alte Spielstand wird ersetzt, nicht vermischt
        if (!_diskIsBase) _storage.DeleteAll();

        foreach ((ChunkCoord coord, Chunk chunk) in _chunks)
        {
            if (!chunk.Modified) continue;
            _storage.Save(coord, chunk.RawBlocks, chunk.Refinements);
            chunk.MarkSaved();
        }

        foreach ((ChunkCoord coord, Chunk chunk) in _keptModified)
            _storage.Save(coord, chunk.RawBlocks, chunk.Refinements);
        _keptModified.Clear();

        _diskIsBase = true;
    }

    /// <summary>Manuelles Laden: verwirft den aktuellen Zustand und stellt den Spielstand her</summary>
    public bool LoadWorld()
    {
        if (!_storage.HasSave) return false;

        _keptModified.Clear();
        _diskIsBase = true;

        // Alles Geladene verwerfen — danach kommt es frisch von Platte bzw. aus dem Generator
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
        else if (_diskIsBase && _storage.TryLoad(coord, Chunk.Size * WorldHeight * Chunk.Size, out byte[]? blocks, out Dictionary<int, ulong[]>? refinements))
        {
            chunk = new Chunk(coord, WorldHeight, blocks, refinements);
        }
        else
        {
            chunk = new Chunk(coord, WorldHeight);
        }

        _chunks.Add(coord, chunk);

        // Selbst + alle 8 Nachbarn neu meshen: Grenzflächen zu vorher "leerem" Nachbarraum
        // verschwinden, und AO an den Rändern stimmt erst mit Nachbardaten
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

    public void Update(Camera3D camera, BoundingBox playerBounds, float buildReach, float sculptRadius)
    {
        if (SculptMode)
        {
            _hasHover = false;
            _hasGhost = false;
            UpdateSculpt(camera, playerBounds, buildReach, sculptRadius);
            return;
        }

        _hasSculptTarget = false;

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
        if (_hasSculptTarget)
            Raylib.DrawSphereWires(_sculptTarget, _sculptRadiusForDraw, 10, 10, new Color(95, 225, 235, 170));
        else if (_hasHover)
            Raylib.DrawCubeWires(_hoverCenter, 1.02f, 1.02f, 1.02f, Color.Yellow);
        else if (_hasGhost)
            Raylib.DrawCubeWires(_ghostCenter, 1f, 1f, 1f, new Color(95, 225, 235, 220));
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

        // Raycast auf Sub-Voxel-Auflösung (Koordinaten x4) — trifft auch durch gebohrte Löcher korrekt
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
            // nichts getroffen → frei in der Luft am Ende der Reichweite formen
            _sculptTarget = ray.Position + Vector3.Normalize(ray.Direction) * buildReach;
        }
        _hasSculptTarget = true;

        _sculptCooldown -= Raylib.GetFrameTime();

        bool carve = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool build = Raylib.IsMouseButtonDown(MouseButton.Right);
        if ((!carve && !build) || _sculptCooldown > 0f) return;

        // Bei beiden Tasten gewinnt das Bohren
        SculptSphere(_sculptTarget, sculptRadius, add: !carve && build, BlockRegistry.Stone, playerBounds);
        _sculptCooldown = SculptInterval;
    }

    /// <summary>Kugel aus Sub-Voxeln entfernen (add=false) bzw. auftragen (add=true)</summary>
    public void SculptSphere(Vector3 center, float radius, bool add, byte blockId, BoundingBox playerBounds)
    {
        float radiusSquared = radius * radius;

        int minBlockX = (int)MathF.Floor(center.X - radius);
        int maxBlockX = (int)MathF.Floor(center.X + radius);
        int minBlockY = Math.Max(0, (int)MathF.Floor(center.Y - radius));
        int maxBlockY = Math.Min(WorldHeight - 1, (int)MathF.Floor(center.Y + radius));
        int minBlockZ = (int)MathF.Floor(center.Z - radius);
        int maxBlockZ = (int)MathF.Floor(center.Z + radius);

        for (int by = minBlockY; by <= maxBlockY; by++)
        for (int bz = minBlockZ; bz <= maxBlockZ; bz++)
        for (int bx = minBlockX; bx <= maxBlockX; bx++)
        {
            var (cc, lx, lz) = WorldToChunk(bx, bz);
            if (!_chunks.TryGetValue(cc, out var chunk)) continue;

            int id = chunk.GetLocal(lx, by, lz, WorldHeight);
            bool solid = BlockRegistry.IsSolid(id);
            if (!add && !solid) continue; // Luft lässt sich nicht weiter aushöhlen

            // Copy-on-Write: gespeicherte Masken nie in-place ändern (Worker-Threads lesen sie)
            ulong[] working = solid
                ? (chunk.TryGetRefinement(lx, by, lz, WorldHeight, out ulong[] existing)
                    ? (ulong[])existing.Clone()
                    : SubVoxels.NewFull())
                : SubVoxels.NewEmpty();

            bool changed = false;

            for (int sz = 0; sz < SubVoxels.Divisions; sz++)
            for (int sy = 0; sy < SubVoxels.Divisions; sy++)
            for (int sx = 0; sx < SubVoxels.Divisions; sx++)
            {
                bool has = SubVoxels.HasBit(working, sx, sy, sz);
                if (add == has) continue; // schon im Zielzustand

                var subCenter = new Vector3(
                    bx + (sx + 0.5f) * SubVoxels.CellSize,
                    by + (sy + 0.5f) * SubVoxels.CellSize,
                    bz + (sz + 0.5f) * SubVoxels.CellSize);

                if (Vector3.DistanceSquared(subCenter, center) > radiusSquared) continue;
                if (add && SubIntersectsBox(playerBounds, bx, by, bz, sx, sy, sz)) continue; // nicht in den Spieler bauen

                if (add) SubVoxels.SetBit(working, sx, sy, sz);
                else SubVoxels.ClearBit(working, sx, sy, sz);
                changed = true;
            }

            if (!changed) continue;

            if (!solid)
            {
                // Luft bekommt Substanz → Block anlegen (SetLocal räumt alte Details mit weg)
                chunk.SetLocal(lx, by, lz, blockId, WorldHeight);
                if (!SubVoxels.IsFull(working))
                    chunk.SetRefinement(lx, by, lz, working, WorldHeight);
            }
            else if (SubVoxels.IsEmpty(working))
            {
                chunk.SetLocal(lx, by, lz, BlockRegistry.Air, WorldHeight); // komplett weggeschnitzt
            }
            else
            {
                chunk.SetRefinement(lx, by, lz, working, WorldHeight); // volle Maske entfernt den Eintrag selbst
            }

            FireDirtyAround(cc, lx, lz);
        }
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

    public bool TryGetRefinement(int wx, int wy, int wz, out ulong[] mask)
    {
        mask = null!;
        if (wy < 0 || wy >= WorldHeight) return false;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return false;

        return chunk.TryGetRefinement(lx, wy, lz, WorldHeight, out mask);
    }

    /// <summary>Blocktyp der Sub-Zelle (Welt-Sub-Koordinaten, 8 pro Block), 0 = Luft</summary>
    public int GetSubVoxel(int swx, int swy, int swz)
    {
        // Shift/LowMask entsprechen FloorDiv/Modulo für die Zweierpotenz (auch für negative Werte)
        int wx = swx >> SubVoxels.Shift;
        int wy = swy >> SubVoxels.Shift;
        int wz = swz >> SubVoxels.Shift;

        if (wy < 0 || wy >= WorldHeight) return 0;

        int id = GetBlock(wx, wy, wz);
        if (!BlockRegistry.IsSolid(id)) return 0;

        if (!TryGetRefinement(wx, wy, wz, out ulong[] mask)) return id; // Vollblock
        return SubVoxels.HasBit(mask, swx & SubVoxels.LowMask, swy & SubVoxels.LowMask, swz & SubVoxels.LowMask) ? id : 0;
    }

    public void SetBlock(int wx, int wy, int wz, int id)
    {
        if (wy < 0 || wy >= WorldHeight) return;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return; // außerhalb der geladenen Welt

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
