using Raylib_cs;
using Terraformer.World;

namespace Terraformer.Rendering;

/// <summary>
/// Baut aus einem gepaddeten Block-Snapshot die Vertex-Daten eines Chunks.
/// Läuft komplett ohne Raylib-Aufrufe und darf deshalb auf einem Worker-Thread laufen.
/// </summary>
public static class ChunkMesher
{
    // Chunk + 1 Voxel Rand in alle Richtungen (für Face-Culling und AO über Chunk-Grenzen)
    public const int PaddedSize = Chunk.Size + 2;

    public static int PaddedLength(int worldHeight) => PaddedSize * PaddedSize * (worldHeight + 2);

    // Nimmt lokale Koordinaten -1..Size bzw. -1..worldHeight entgegen
    public static int Index(int x, int y, int z) => (x + 1) + PaddedSize * ((z + 1) + PaddedSize * (y + 1));

    private struct FaceInfo
    {
        public int Nx, Ny, Nz;                  // Normale
        public int Ux, Uy, Uz;                  // Tangente u (für AO)
        public int Vx, Vy, Vz;                  // Tangente v (für AO)
        public (int X, int Y, int Z)[] Corners; // 4 Ecken, gegen den Uhrzeigersinn von außen gesehen
        public float Shade;                     // gebackenes Richtungs-Ambient (oben hell, unten dunkel)
    }

    private static readonly FaceInfo[] _faces =
    {
        new() { Nx = 0, Ny = 1, Nz = 0, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 0, Vz = 1, Shade = 1.00f,
                Corners = new[] { (0, 1, 1), (1, 1, 1), (1, 1, 0), (0, 1, 0) } },
        new() { Nx = 0, Ny = -1, Nz = 0, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 0, Vz = 1, Shade = 0.55f,
                Corners = new[] { (0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1) } },
        new() { Nx = 1, Ny = 0, Nz = 0, Ux = 0, Uy = 0, Uz = 1, Vx = 0, Vy = 1, Vz = 0, Shade = 0.80f,
                Corners = new[] { (1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1) } },
        new() { Nx = -1, Ny = 0, Nz = 0, Ux = 0, Uy = 0, Uz = 1, Vx = 0, Vy = 1, Vz = 0, Shade = 0.80f,
                Corners = new[] { (0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0) } },
        new() { Nx = 0, Ny = 0, Nz = 1, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 1, Vz = 0, Shade = 0.72f,
                Corners = new[] { (1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1) } },
        new() { Nx = 0, Ny = 0, Nz = -1, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 1, Vz = 0, Shade = 0.72f,
                Corners = new[] { (0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0) } },
    };

    // Zwei Dreiecke pro Quad; Diagonale wird nach AO gewählt (sonst Anisotropie-Artefakte)
    private static readonly int[] _quadOrder = { 0, 1, 2, 0, 2, 3 };
    private static readonly int[] _quadOrderFlipped = { 1, 2, 3, 1, 3, 0 };

    public static ChunkMeshData Build(byte[] padded, Dictionary<int, ulong> refinements, int worldHeight, int worldX, int worldZ)
    {
        var vertices = new List<float>(24576);
        var normals = new List<float>(24576);
        var colors = new List<byte>(32768);

        for (int y = 0; y < worldHeight; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int id = padded[Index(x, y, z)];
            if (!BlockRegistry.IsSolid(id)) continue;

            bool refined = refinements.TryGetValue(Index(x, y, z), out ulong ownMask);

            if (!refined)
            {
                bool exposed =
                    !NeighborOccludes(padded, refinements, x + 1, y, z, 2) ||
                    !NeighborOccludes(padded, refinements, x - 1, y, z, 3) ||
                    !NeighborOccludes(padded, refinements, x, y + 1, z, 0) ||
                    !NeighborOccludes(padded, refinements, x, y - 1, z, 1) ||
                    !NeighborOccludes(padded, refinements, x, y, z + 1, 4) ||
                    !NeighborOccludes(padded, refinements, x, y, z - 1, 5);
                if (!exposed) continue;
            }

            Color albedo = TerrainColors.ForBlock(id, worldX + x, y, worldZ + z);
            byte emissive = (byte)(Math.Clamp(BlockRegistry.Get(id).Emissive, 0f, 1f) * 255f);

            if (refined)
            {
                EmitRefinedBlock(vertices, normals, colors, padded, refinements, x, y, z, ownMask, albedo, emissive);
                continue;
            }

            for (int f = 0; f < _faces.Length; f++)
            {
                ref readonly FaceInfo face = ref _faces[f];
                if (NeighborOccludes(padded, refinements, x + face.Nx, y + face.Ny, z + face.Nz, f)) continue;

                EmitFace(vertices, normals, colors, padded, in face, x, y, z, albedo, emissive);
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

    // Ein Nachbar verdeckt eine Fläche nur, wenn er solide ist UND seine zugewandte
    // Sub-Voxel-Randschicht komplett gefüllt ist (Vollblöcke sind implizit voll)
    private static bool NeighborOccludes(byte[] padded, Dictionary<int, ulong> refinements, int nx, int ny, int nz, int faceIndex)
    {
        int index = Index(nx, ny, nz);
        if (!BlockRegistry.IsSolid(padded[index])) return false;
        if (!refinements.TryGetValue(index, out ulong mask)) return true;

        ulong layer = SubVoxels.FaceLayers[faceIndex ^ 1];
        return (mask & layer) == layer;
    }

    private static void EmitRefinedBlock(
        List<float> vertices,
        List<float> normals,
        List<byte> colors,
        byte[] padded,
        Dictionary<int, ulong> refinements,
        int x, int y, int z,
        ulong mask,
        Color albedo,
        byte emissive)
    {
        for (int sz = 0; sz < SubVoxels.Divisions; sz++)
        for (int sy = 0; sy < SubVoxels.Divisions; sy++)
        for (int sx = 0; sx < SubVoxels.Divisions; sx++)
        {
            if (!SubVoxels.HasBit(mask, sx, sy, sz)) continue;

            for (int f = 0; f < _faces.Length; f++)
            {
                ref readonly FaceInfo face = ref _faces[f];

                int nsx = sx + face.Nx;
                int nsy = sy + face.Ny;
                int nsz = sz + face.Nz;

                bool occluded;
                if (nsx >= 0 && nsx < SubVoxels.Divisions &&
                    nsy >= 0 && nsy < SubVoxels.Divisions &&
                    nsz >= 0 && nsz < SubVoxels.Divisions)
                {
                    occluded = SubVoxels.HasBit(mask, nsx, nsy, nsz);
                }
                else
                {
                    // Über die Blockgrenze: das zugewandte Sub-Voxel des Nachbarblocks prüfen
                    int index = Index(x + face.Nx, y + face.Ny, z + face.Nz);
                    if (!BlockRegistry.IsSolid(padded[index]))
                    {
                        occluded = false;
                    }
                    else if (!refinements.TryGetValue(index, out ulong neighborMask))
                    {
                        occluded = true; // Vollblock
                    }
                    else
                    {
                        occluded = SubVoxels.HasBit(
                            neighborMask,
                            (nsx + SubVoxels.Divisions) & 3,
                            (nsy + SubVoxels.Divisions) & 3,
                            (nsz + SubVoxels.Divisions) & 3);
                    }
                }

                if (occluded) continue;

                EmitSubFace(vertices, normals, colors, in face, x, y, z, sx, sy, sz, albedo, emissive);
            }
        }
    }

    private static void EmitSubFace(
        List<float> vertices,
        List<float> normals,
        List<byte> colors,
        in FaceInfo face,
        int x, int y, int z,
        int sx, int sy, int sz,
        Color albedo,
        byte emissive)
    {
        const float cell = SubVoxels.CellSize;
        float originX = x + sx * cell;
        float originY = y + sy * cell;
        float originZ = z + sz * cell;

        // Kein Sub-Voxel-AO — leicht abgedunkelt, damit Höhlungen nicht flach-hell wirken
        float light = face.Shade * 0.92f;
        byte r = (byte)(albedo.R * light);
        byte g = (byte)(albedo.G * light);
        byte b = (byte)(albedo.B * light);

        for (int i = 0; i < _quadOrder.Length; i++)
        {
            (int cx, int cy, int cz) = face.Corners[_quadOrder[i]];

            vertices.Add(originX + cx * cell);
            vertices.Add(originY + cy * cell);
            vertices.Add(originZ + cz * cell);

            normals.Add(face.Nx);
            normals.Add(face.Ny);
            normals.Add(face.Nz);

            colors.Add(r);
            colors.Add(g);
            colors.Add(b);
            colors.Add(emissive);
        }
    }

    private static void EmitFace(
        List<float> vertices,
        List<float> normals,
        List<byte> colors,
        byte[] padded,
        in FaceInfo face,
        int x, int y, int z,
        Color albedo,
        byte emissive)
    {
        // Luftzelle vor der Fläche — von dort aus werden die AO-Nachbarn abgetastet
        int airX = x + face.Nx;
        int airY = y + face.Ny;
        int airZ = z + face.Nz;

        Span<int> ambientOcclusion = stackalloc int[4];

        for (int i = 0; i < 4; i++)
        {
            (int cx, int cy, int cz) = face.Corners[i];

            // Vorzeichen der Tangenten Richtung dieser Ecke
            int signU = (cx * face.Ux + cy * face.Uy + cz * face.Uz) == 1 ? 1 : -1;
            int signV = (cx * face.Vx + cy * face.Vy + cz * face.Vz) == 1 ? 1 : -1;

            bool side1 = BlockRegistry.IsSolid(padded[Index(
                airX + signU * face.Ux, airY + signU * face.Uy, airZ + signU * face.Uz)]);
            bool side2 = BlockRegistry.IsSolid(padded[Index(
                airX + signV * face.Vx, airY + signV * face.Vy, airZ + signV * face.Vz)]);
            bool corner = BlockRegistry.IsSolid(padded[Index(
                airX + signU * face.Ux + signV * face.Vx,
                airY + signU * face.Uy + signV * face.Vy,
                airZ + signU * face.Uz + signV * face.Vz)]);

            ambientOcclusion[i] = (side1 && side2)
                ? 0
                : 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
        }

        int[] order = ambientOcclusion[0] + ambientOcclusion[2] >= ambientOcclusion[1] + ambientOcclusion[3]
            ? _quadOrder
            : _quadOrderFlipped;

        for (int i = 0; i < order.Length; i++)
        {
            int cornerIndex = order[i];
            (int cx, int cy, int cz) = face.Corners[cornerIndex];

            vertices.Add(x + cx);
            vertices.Add(y + cy);
            vertices.Add(z + cz);

            normals.Add(face.Nx);
            normals.Add(face.Ny);
            normals.Add(face.Nz);

            float light = face.Shade * (0.45f + 0.55f * (ambientOcclusion[cornerIndex] / 3f));
            colors.Add((byte)(albedo.R * light));
            colors.Add((byte)(albedo.G * light));
            colors.Add((byte)(albedo.B * light));
            colors.Add(emissive);
        }
    }
}
