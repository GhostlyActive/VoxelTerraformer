using Raylib_cs;
using System.Numerics;
using Terraformer.World;

namespace Terraformer.Rendering;

/// <summary>
/// Dritte Voxel-Stufe: Marching Cubes über einem Dichtefeld, das aus den binären
/// Sub-Voxeln abgeleitet wird (Box-Filter). Ergebnis ist eine runde, glatte Oberfläche
/// mit weichen Normalen aus dem Dichtegradienten — die Weltdaten bleiben unverändert,
/// deshalb ist das Umschalten zwischen den Modi verlustfrei.
/// </summary>
public static class SmoothChunkMesher
{
    /// <summary>
    /// MC-Zellen pro Block; muss 8 teilen (1, 2, 4 oder 8).
    /// 2 → 0,5 m Zellen: sehr weiche Landschaft, ~8 Mio. Vertices bei voller Sichtweite.
    /// 4 → 0,25 m Zellen: mehr Feinheit, aber die vierfache Geometrie.
    /// </summary>
    public const int Divisions = 2;

    private const int SubPerCell = SubVoxels.Divisions / Divisions;
    private const float CellSize = 1f / Divisions;
    private const float Iso = 0.5f;

    // Dichtegitter je Block: Indizes -1 .. Divisions+1 (ein Ring extra für die Gradienten)
    private const int GridSize = Divisions + 3;
    private const int GridOffset = 1;

    public static ChunkMeshData Build(
        byte[] padded,
        Dictionary<int, ulong[]> refinements,
        int worldHeight,
        int worldX,
        int worldZ)
    {
        var vertices = new List<float>(16384);
        var normals = new List<float>(16384);
        var colors = new List<byte>(24576);

        var neighborId = new byte[27];
        var neighborSolid = new bool[27];
        var neighborMask = new ulong[27][];
        var density = new float[GridSize * GridSize * GridSize];

        Span<float> cornerDensity = stackalloc float[8];
        Span<Vector3> cornerNormal = stackalloc Vector3[8];
        Span<Vector3> edgePosition = stackalloc Vector3[12];
        Span<Vector3> edgeNormal = stackalloc Vector3[12];

        for (int y = 0; y < worldHeight; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            if (!GatherNeighborhood(padded, refinements, x, y, z, neighborId, neighborSolid, neighborMask))
                continue;

            BuildDensity(neighborSolid, neighborMask, density);

            (Color albedo, byte emissive) = SurfaceMaterial(neighborId, neighborSolid, x, y, z, worldX, worldZ);

            for (int cz = 0; cz < Divisions; cz++)
            for (int cy = 0; cy < Divisions; cy++)
            for (int cx = 0; cx < Divisions; cx++)
            {
                int cubeIndex = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    int gx = cx + MarchingCubesTables.CornerX[corner];
                    int gy = cy + MarchingCubesTables.CornerY[corner];
                    int gz = cz + MarchingCubesTables.CornerZ[corner];

                    float value = density[GridIndex(gx, gy, gz)];
                    cornerDensity[corner] = value;
                    if (value < Iso) cubeIndex |= 1 << corner;
                }

                int edges = MarchingCubesTables.EdgeMask[cubeIndex];
                if (edges == 0) continue;

                for (int corner = 0; corner < 8; corner++)
                {
                    if ((edges & CornerEdgeMask[corner]) == 0) continue;
                    cornerNormal[corner] = GradientNormal(
                        density,
                        cx + MarchingCubesTables.CornerX[corner],
                        cy + MarchingCubesTables.CornerY[corner],
                        cz + MarchingCubesTables.CornerZ[corner]);
                }

                for (int edge = 0; edge < 12; edge++)
                {
                    if ((edges & (1 << edge)) == 0) continue;

                    int a = MarchingCubesTables.EdgeCornerA[edge];
                    int b = MarchingCubesTables.EdgeCornerB[edge];

                    float da = cornerDensity[a];
                    float db = cornerDensity[b];
                    float t = MathF.Abs(db - da) < 1e-6f ? 0.5f : (Iso - da) / (db - da);
                    t = Math.Clamp(t, 0f, 1f);

                    var pa = new Vector3(
                        x + (cx + MarchingCubesTables.CornerX[a]) * CellSize,
                        y + (cy + MarchingCubesTables.CornerY[a]) * CellSize,
                        z + (cz + MarchingCubesTables.CornerZ[a]) * CellSize);
                    var pb = new Vector3(
                        x + (cx + MarchingCubesTables.CornerX[b]) * CellSize,
                        y + (cy + MarchingCubesTables.CornerY[b]) * CellSize,
                        z + (cz + MarchingCubesTables.CornerZ[b]) * CellSize);

                    edgePosition[edge] = Vector3.Lerp(pa, pb, t);
                    edgeNormal[edge] = SafeNormalize(Vector3.Lerp(cornerNormal[a], cornerNormal[b], t));
                }

                for (int i = 0; i < 15; i += 3)
                {
                    int e0 = MarchingCubesTables.TriTable[cubeIndex * 16 + i];
                    if (e0 < 0) break;

                    int e1 = MarchingCubesTables.TriTable[cubeIndex * 16 + i + 1];
                    int e2 = MarchingCubesTables.TriTable[cubeIndex * 16 + i + 2];

                    // Die Tabelle setzt "innen" = unterhalb des Iso-Werts; bei uns ist innen die
                    // hohe Dichte, deshalb die Reihenfolge drehen — sonst zeigen alle Flächen nach innen
                    Emit(vertices, normals, colors, edgePosition[e0], edgeNormal[e0], albedo, emissive);
                    Emit(vertices, normals, colors, edgePosition[e2], edgeNormal[e2], albedo, emissive);
                    Emit(vertices, normals, colors, edgePosition[e1], edgeNormal[e1], albedo, emissive);
                }
            }
        }

        return new ChunkMeshData
        {
            Vertices = vertices.ToArray(),
            Normals = normals.ToArray(),
            Colors = colors.ToArray(),
            VertexCount = vertices.Count / 3,
        };
    }

    private static void Emit(
        List<float> vertices, List<float> normals, List<byte> colors,
        Vector3 position, Vector3 normal, Color albedo, byte emissive)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);

        normals.Add(normal.X);
        normals.Add(normal.Y);
        normals.Add(normal.Z);

        colors.Add(albedo.R);
        colors.Add(albedo.G);
        colors.Add(albedo.B);
        colors.Add(emissive);
    }

    /// <summary>
    /// Blocktypen und Sub-Voxel-Masken der 3x3x3-Nachbarschaft einsammeln.
    /// Liefert false, wenn hier keine Oberfläche verlaufen kann (alles voll oder alles leer).
    /// </summary>
    private static bool GatherNeighborhood(
        byte[] padded,
        Dictionary<int, ulong[]> refinements,
        int x, int y, int z,
        byte[] neighborId,
        bool[] neighborSolid,
        ulong[]?[] neighborMask)
    {
        bool anySolid = false;
        bool anyOpen = false;

        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int slot = (dx + 1) + 3 * ((dy + 1) + 3 * (dz + 1));
            int index = ChunkMesher.Index(x + dx, y + dy, z + dz);

            byte id = padded[index];
            bool isSolid = BlockRegistry.IsSolid(id);

            neighborId[slot] = id;
            neighborSolid[slot] = isSolid;
            neighborMask[slot] = null;

            if (isSolid)
            {
                anySolid = true;
                if (refinements.TryGetValue(index, out ulong[]? mask))
                {
                    neighborMask[slot] = mask;
                    anyOpen = true; // angeschnitzt → enthält auch Leerraum
                }
            }
            else
            {
                anyOpen = true;
            }
        }

        return anySolid && anyOpen;
    }

    /// <summary>
    /// Dichte an jedem Gitterpunkt = Mittel der 2x2x2 umliegenden Sub-Voxel.
    /// Rein positionsabhängig, deshalb stimmen benachbarte Blöcke an ihren Grenzen überein.
    /// </summary>
    /// <summary>
    /// Dichte an jedem Gitterpunkt = Mittel der SubPerCell³ umliegenden Sub-Voxel.
    /// Die Boxen benachbarter Gitterpunkte kacheln lückenlos, jedes Sub-Voxel zählt also genau einmal.
    /// Rein positionsabhängig, deshalb stimmen benachbarte Blöcke an ihren Grenzen überein.
    /// </summary>
    private static void BuildDensity(bool[] neighborSolid, ulong[]?[] neighborMask, float[] density)
    {
        const float inverseTaps = 1f / (SubPerCell * SubPerCell * SubPerCell);

        for (int gz = -GridOffset; gz < GridSize - GridOffset; gz++)
        for (int gy = -GridOffset; gy < GridSize - GridOffset; gy++)
        for (int gx = -GridOffset; gx < GridSize - GridOffset; gx++)
        {
            int sx = gx * SubPerCell;
            int sy = gy * SubPerCell;
            int sz = gz * SubPerCell;

            int sum = 0;
            for (int oz = -SubPerCell / 2; oz < SubPerCell / 2; oz++)
            for (int oy = -SubPerCell / 2; oy < SubPerCell / 2; oy++)
            for (int ox = -SubPerCell / 2; ox < SubPerCell / 2; ox++)
                sum += Occupancy(neighborSolid, neighborMask, sx + ox, sy + oy, sz + oz);

            density[GridIndex(gx, gy, gz)] = sum * inverseTaps;
        }
    }

    /// <summary>1 = solide, 0 = leer; Koordinaten sind block-lokale Sub-Voxel (dürfen in die Nachbarblöcke reichen)</summary>
    private static int Occupancy(bool[] neighborSolid, ulong[]?[] neighborMask, int sx, int sy, int sz)
    {
        int slot = ((sx >> SubVoxels.Shift) + 1)
                 + 3 * (((sy >> SubVoxels.Shift) + 1)
                 + 3 * ((sz >> SubVoxels.Shift) + 1));

        ulong[]? mask = neighborMask[slot];
        if (mask == null) return neighborSolid[slot] ? 1 : 0;

        return SubVoxels.HasBit(mask, sx & SubVoxels.LowMask, sy & SubVoxels.LowMask, sz & SubVoxels.LowMask) ? 1 : 0;
    }

    /// <summary>Nach außen zeigende Normale = entgegen dem Dichtegradienten</summary>
    private static Vector3 GradientNormal(float[] density, int gx, int gy, int gz)
    {
        var gradient = new Vector3(
            density[GridIndex(gx + 1, gy, gz)] - density[GridIndex(gx - 1, gy, gz)],
            density[GridIndex(gx, gy + 1, gz)] - density[GridIndex(gx, gy - 1, gz)],
            density[GridIndex(gx, gy, gz + 1)] - density[GridIndex(gx, gy, gz - 1)]);

        return SafeNormalize(-gradient);
    }

    private static Vector3 SafeNormalize(Vector3 v)
        => v.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(v);

    /// <summary>Farbe des Blocks, der die Oberfläche trägt — bei Luft der nächste solide Nachbar (Boden bevorzugt)</summary>
    private static (Color Albedo, byte Emissive) SurfaceMaterial(
        byte[] neighborId, bool[] neighborSolid, int x, int y, int z, int worldX, int worldZ)
    {
        const int below = 10; // Slot von (0,-1,0) — Böden sollen ihre eigene Farbe behalten

        int bestSlot = 13; // Mitte
        if (!neighborSolid[13])
        {
            bestSlot = neighborSolid[below] ? below : -1;
            for (int slot = 0; slot < 27 && bestSlot < 0; slot++)
                if (neighborSolid[slot])
                    bestSlot = slot;

            if (bestSlot < 0) return (Color.Magenta, 0); // kann nicht auftreten: GatherNeighborhood verlangt anySolid
        }

        int dx = bestSlot % 3 - 1;
        int dy = bestSlot / 3 % 3 - 1;
        int dz = bestSlot / 9 - 1;

        byte id = neighborId[bestSlot];
        Color albedo = TerrainColors.ForBlock(id, worldX + x + dx, y + dy, worldZ + z + dz);
        byte emissive = (byte)(Math.Clamp(BlockRegistry.Get(id).Emissive, 0f, 1f) * 255f);

        return (albedo, emissive);
    }

    private static int GridIndex(int gx, int gy, int gz)
        => (gx + GridOffset) + GridSize * ((gy + GridOffset) + GridSize * (gz + GridOffset));

    // Welche Kanten berühren welche Ecke — spart das Berechnen ungenutzter Gradienten
    private static readonly int[] CornerEdgeMask = BuildCornerEdgeMask();

    private static int[] BuildCornerEdgeMask()
    {
        var masks = new int[8];
        for (int edge = 0; edge < 12; edge++)
        {
            masks[MarchingCubesTables.EdgeCornerA[edge]] |= 1 << edge;
            masks[MarchingCubesTables.EdgeCornerB[edge]] |= 1 << edge;
        }
        return masks;
    }
}
