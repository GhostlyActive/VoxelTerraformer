using Raylib_cs;
using System.Numerics;
using Terraformer.Config;
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

    // Sculpt-Modus: Kugel-Brush auf dem Sub-Voxel-Dichtefeld, halten = durchgehender Strich
    private bool _hasSculptTarget;
    private Vector3 _sculptTarget;
    private float _sculptRadiusForDraw;

    // Zwischen zwei Frames wandert das Ziel; ohne Nachstempeln entlang der Strecke
    // entstünde bei schneller Mausbewegung eine Perlenkette statt einer Röhre
    private bool _stroking;
    private Vector3 _strokePrevious;
    private bool _strokeAdding;
    private float _strokeParticleCooldown;
    private DebugSettings _settings = new();

    // Beim Auftragen folgt der Pinsel dem Fadenkreuz nur mit begrenztem Tempo. Sonst schnellt
    // die Fläche dem Blick entgegen, sobald der Raycast auf dem frisch gebauten Material landet.
    private bool _hasBuildFront;
    private Vector3 _buildFront;
    private bool _rayHadHit;

    // Pinselkugel und Blockrahmen stören beim Bauen — sie erscheinen nur kurz nach einer
    // Größenänderung (Mausrad) und dauerhaft, solange die Debug-Anzeige läuft
    private float _previewTimer;

    private const float StrokeStepFactor = 0.5f;    // Stempelabstand als Anteil des Radius
    private const float StrokeMaxJump = 6f;         // darüber war es ein Zielsprung, keine Handbewegung
    private const int StrokeMaxStamps = 12;
    private const float BuildMaxLag = 1.5f;         // Rückstand der Baufront, in Radien
    private const float SculptParticleInterval = 0.07f;

    /// <summary>Umschaltbar per Taste V — siehe <see cref="TerrainMode"/></summary>
    public TerrainMode Mode { get; set; } = TerrainMode.Blocks;

    /// <summary>Beide Feinmodi benutzen denselben Kugel-Brush, nur die Darstellung unterscheidet sich</summary>
    public bool UsesSculptTool => Mode != TerrainMode.Blocks;

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
    public bool SaveWorld()
    {
        // Frische Session: der alte Spielstand wird ersetzt, nicht vermischt
        if (!_diskIsBase) _storage.DeleteAll();

        bool allWritten = true;

        foreach ((ChunkCoord coord, Chunk chunk) in _chunks)
        {
            if (!chunk.Modified) continue;
            allWritten &= _storage.Save(coord, chunk.RawBlocks, chunk.Refinements);
        }

        foreach ((ChunkCoord coord, Chunk chunk) in _keptModified)
            allWritten &= _storage.Save(coord, chunk.RawBlocks, chunk.Refinements);

        // Bei Fehlschlag Zustand unangetastet lassen — der nächste Versuch speichert wieder alles
        if (!allWritten) return false;

        foreach ((ChunkCoord _, Chunk chunk) in _chunks)
            if (chunk.Modified) chunk.MarkSaved();
        _keptModified.Clear();

        _diskIsBase = true;
        return true;
    }

    /// <summary>Manuelles Laden: verwirft den aktuellen Zustand und stellt den Spielstand her</summary>
    public bool LoadWorld()
    {
        // Alte/fremde Formatversionen ablehnen — sonst würde still eine frische Welt geladen
        if (!_storage.HasCompatibleSave) return false;

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
        else if (_diskIsBase && _storage.TryLoad(coord, Chunk.Size * WorldHeight * Chunk.Size, out byte[]? blocks, out Dictionary<int, byte[]>? refinements))
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

    public void Update(Camera3D camera, BoundingBox playerBounds, DebugSettings settings)
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

        // Hover/Ghost jeden Frame aktualisieren — Abbauen/Bauen wirken exakt auf das markierte Ziel
        UpdateHover(camera, settings.BuildReach);

        if (remove) TryRemove();
        if (place)  TryPlace(playerBounds);
    }

    /// <summary>Dauerhafte Vorschau (Blockrahmen bzw. Pinselkugel) — hängt an der Debug-Anzeige (F3)</summary>
    public bool ShowPreviewAlways { get; set; }

    /// <summary>Vorschau kurz einblenden, z. B. nachdem sich Reichweite oder Brushgröße geändert haben</summary>
    public void PulsePreview() => _previewTimer = Math.Max(_previewTimer, _settings.PreviewHold);

    public void DrawHover()
    {
        // Am Ende ausblenden statt hart abschalten — ein Sprung fällt mehr auf als die Vorschau selbst
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
    /// Pinselvorschau: durchscheinende Kugel plus Drahtgitter, grün beim Auftragen,
    /// rot beim Abtragen. Der Puls macht sichtbar, dass der Strich gerade läuft.
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
        Raylib.DrawSphere(_sculptTarget, radius, Fade(tint, (active ? 60 : 34) / 255f * visibility));
        Raylib.EndBlendMode();

        Raylib.DrawSphereWires(_sculptTarget, radius, 12, 12, Fade(tint, (active ? 200 : 150) / 255f * visibility));
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

        // Raycast auf Sub-Voxel-Auflösung (Koordinaten x8) — trifft auch durch gebohrte Löcher korrekt
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

        // Trefferlage jeden Frame fortschreiben, auch ohne gedrückte Taste
        bool rayJumped = hit.HasHit != _rayHadHit;
        _rayHadHit = hit.HasHit;

        bool carve = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool build = Raylib.IsMouseButtonDown(MouseButton.Right);

        if (!carve && !build)
        {
            _stroking = false;
            _hasBuildFront = false;
            return;
        }

        bool add = !carve && build; // bei beiden Tasten gewinnt das Bohren
        if (_stroking && add != _strokeAdding) _stroking = false;

        float frameTime = Raylib.GetFrameTime();
        _strokeAdding = add;
        _strokeParticleCooldown -= frameTime;

        // Der kantige Sculpt-Modus schaltet Zellen hart (sonst zerfranst das Würfelbild),
        // Smooth trägt eine weiche Flanke auf — daraus zieht Marching Cubes die runde Fläche
        float edge = Mode == TerrainMode.Smooth ? sculptRadius * _settings.BrushSoftness : 0f;

        // Wechselt der Strahl zwischen Treffer und Leere, springt das Ziel um Meter, ohne dass
        // die Hand etwas getan hat — dort neu ansetzen, sonst zieht die Front eine Röhre quer
        // durch die Luft hinterher. Reine Handbewegung lässt sie dagegen zurückfallen.
        if (add && rayJumped)
        {
            _hasBuildFront = false;
            _stroking = false;
        }

        // Abtragen wirkt sofort am Ziel — beim Graben stört jede Verzögerung. Auftragen
        // kriecht dorthin, damit die Fläche stetig wächst statt in ganzen Kugeln zu springen.
        Vector3 point = add ? AdvanceBuildFront(_sculptTarget, sculptRadius, frameTime) : _sculptTarget;
        if (!add) _hasBuildFront = false;

        bool changed = StampStroke(point, sculptRadius, add, edge, playerBounds);

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
    /// Die Baufront zieht mit dem eingestellten Tempo hinter dem Fadenkreuz her. Wer die Maus
    /// schneller bewegt, läuft ihr davon — dann bleibt sie sichtbar zurück, statt zum Ziel zu
    /// springen und die übersprungene Strecke in einem Frame zuzuschütten. Weiter als
    /// <see cref="BuildMaxLag"/> Radien fällt sie nicht zurück, sonst baut man blind hinterher.
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
    /// Stempelt den Brush entlang der Strecke seit dem letzten Frame. Ein einzelner Stempel
    /// pro Frame würde bei schneller Mausbewegung eine Perlenkette hinterlassen; überlappende
    /// Kugeln ergeben dagegen eine durchgehende Röhre.
    /// </summary>
    private bool StampStroke(Vector3 target, float radius, bool add, float edge, BoundingBox playerBounds)
    {
        bool changed = SculptBlob(target, radius, add, edge, BlockRegistry.Stone, playerBounds);
        if (!_stroking) return changed;

        Vector3 delta = target - _strokePrevious;
        float distance = delta.Length();

        // Große Sprünge kommen vom Raycast (Ziel wechselt zwischen Treffer und Reichweitenende),
        // nicht von einer Handbewegung — die dürfen keine Spur ziehen
        float step = radius * StrokeStepFactor;
        if (distance < step || distance > radius * StrokeMaxJump) return changed;

        int stamps = Math.Min((int)(distance / step), StrokeMaxStamps);
        for (int i = 1; i <= stamps; i++)
        {
            Vector3 point = _strokePrevious + delta * (i / (float)(stamps + 1));
            changed |= SculptBlob(point, radius, add, edge, BlockRegistry.Stone, playerBounds);
        }

        return changed;
    }

    /// <summary>
    /// Weicher Kugel-Brush auf dem Sub-Voxel-Dichtefeld: im Kern 255, am Radius genau der
    /// Iso-Wert, nach außen über <paramref name="edge"/> Meter auf 0 auslaufend.
    /// Auftragen vereinigt (Maximum), Abtragen subtrahiert (Minimum) — dadurch wachsen
    /// mehrere Striche zu einer runden Form zusammen, statt einander zu überschreiben,
    /// und ein Stempel auf dieselbe Stelle ändert nichts mehr.
    /// </summary>
    public bool SculptBlob(Vector3 center, float radius, bool add, float edge, byte blockId, BoundingBox playerBounds)
    {
        // Über 2*radius Flanke erreicht der Kern nie volle Dichte — der Brush träfe ins Leere
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
            if (BlockDistanceSquared(bx, by, bz, center) > reach * reach) continue; // Ecke der Box, außerhalb der Kugel

            var (cc, lx, lz) = WorldToChunk(bx, bz);
            if (!_chunks.TryGetValue(cc, out var chunk)) continue;

            int id = chunk.GetLocal(lx, by, lz, WorldHeight);
            bool solid = BlockRegistry.IsSolid(id);
            if (!add && !solid) continue; // Luft lässt sich nicht weiter aushöhlen

            // Copy-on-Write: gespeicherte Felder nie in-place ändern (Worker-Threads lesen sie)
            bool refined = chunk.TryGetRefinement(lx, by, lz, WorldHeight, out byte[] existing);
            byte[] working = solid
                ? (refined ? (byte[])existing.Clone() : SubVoxels.NewFull())
                : SubVoxels.NewEmpty();

            bool changed = false;
            bool crossesIso = false;

            // Nur die Zellen ablaufen, die der Pinsel überhaupt erreichen kann — bei großem
            // Radius liegen sonst die meisten der 512 Zellen je Block nutzlos in der Schleife
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
                if (distanceSquared > reach * reach) continue; // spart die Wurzel für die Ecken

                float profile = Profile(MathF.Sqrt(distanceSquared), radius, edge);
                if (profile <= 0f) continue;

                byte previous = SubVoxels.Get(working, sx, sy, sz);
                float wanted = add
                    ? MathF.Max(previous, profile * 255f)
                    : MathF.Min(previous, (1f - profile) * 255f);

                byte next = SubVoxels.Quantize(wanted);
                if (next == previous) continue;
                if (next > previous && SubIntersectsBox(playerBounds, bx, by, bz, sx, sy, sz)) continue; // nicht in den Spieler bauen

                SubVoxels.Set(working, sx, sy, sz, next);
                changed = true;
                if (add ? next >= SubVoxels.Iso : next < SubVoxels.Iso) crossesIso = true;
            }

            if (!changed) continue;

            // Nur Flankensaum in einem noch unberührten Block: die Form selbst reicht nicht bis
            // hierher. Die Dichte trotzdem einzutragen verschiebt bloß den Box-Filter der
            // Nachbarschaft — und weil eine ebene Blockoberfläche exakt auf der Iso-Kante liegt,
            // kippen daraus hauchdünne Häute bzw. Kerben neben der Form heraus.
            if (!crossesIso && !refined) continue;

            anyChange = true;

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
                chunk.SetRefinement(lx, by, lz, working, WorldHeight); // volles Feld entfernt den Eintrag selbst
            }

            FireDirtyAround(cc, lx, lz);
        }

        return anyChange;
    }

    /// <summary>Füllgrad 0..1 der Pinselkugel: im Kern 1, bei distance == radius genau 0,5 (= Iso), außen 0</summary>
    private static float Profile(float distance, float radius, float edge)
    {
        if (edge <= 0f) return distance <= radius ? 1f : 0f;

        float t = 0.5f + (radius - distance) / edge;
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        return t * t * (3f - 2f * t); // Smoothstep: Flanke ohne Knick, sonst zeichnet sich der Pinselrand ab
    }

    /// <summary>Zellen eines Blocks, die auf einer Achse in Reichweite des Pinselzentrums liegen</summary>
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

    public bool TryGetRefinement(int wx, int wy, int wz, out byte[] field)
    {
        field = null!;
        if (wy < 0 || wy >= WorldHeight) return false;

        var (cc, lx, lz) = WorldToChunk(wx, wz);
        if (!_chunks.TryGetValue(cc, out var chunk)) return false;

        return chunk.TryGetRefinement(lx, wy, lz, WorldHeight, out field);
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

        if (!TryGetRefinement(wx, wy, wz, out byte[] field)) return id; // Vollblock
        return SubVoxels.IsSolid(field, swx & SubVoxels.LowMask, swy & SubVoxels.LowMask, swz & SubVoxels.LowMask) ? id : 0;
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
