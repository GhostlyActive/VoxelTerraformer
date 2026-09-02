using Raylib_cs;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Builds the mesh of one sub-chunk of a <see cref="VoxelBody"/>. Bodies carry no sub-voxel
/// density fields, so this is the plain blocky mesher: one quad per exposed face, with ambient
/// occlusion baked into the vertex colours from the neighbours around each corner.
///
/// Unlike the terrain mesher there is no directional shade per face. On a sphere every face
/// direction is equally valid, so a baked "top is bright" would fight the sunlight the shader
/// computes.
/// </summary>
public static class VoxelBodyMesher
{
    private readonly struct Face
    {
        public required int Nx { get; init; }
        public required int Ny { get; init; }
        public required int Nz { get; init; }
        public required int Ux { get; init; }
        public required int Uy { get; init; }
        public required int Uz { get; init; }
        public required int Vx { get; init; }
        public required int Vy { get; init; }
        public required int Vz { get; init; }

        /// <summary>4 corners, counter-clockwise seen from outside</summary>
        public required (int X, int Y, int Z)[] Corners { get; init; }
    }

    private static readonly Face[] _faces =
    {
        new() { Nx = 0, Ny = 1, Nz = 0, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 0, Vz = 1,
                Corners = new[] { (0, 1, 1), (1, 1, 1), (1, 1, 0), (0, 1, 0) } },
        new() { Nx = 0, Ny = -1, Nz = 0, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 0, Vz = 1,
                Corners = new[] { (0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1) } },
        new() { Nx = 1, Ny = 0, Nz = 0, Ux = 0, Uy = 0, Uz = 1, Vx = 0, Vy = 1, Vz = 0,
                Corners = new[] { (1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1) } },
        new() { Nx = -1, Ny = 0, Nz = 0, Ux = 0, Uy = 0, Uz = 1, Vx = 0, Vy = 1, Vz = 0,
                Corners = new[] { (0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0) } },
        new() { Nx = 0, Ny = 0, Nz = 1, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 1, Vz = 0,
                Corners = new[] { (1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1) } },
        new() { Nx = 0, Ny = 0, Nz = -1, Ux = 1, Uy = 0, Uz = 0, Vx = 0, Vy = 1, Vz = 0,
                Corners = new[] { (0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0) } },
    };

    private static readonly int[] _quadOrder = { 0, 1, 2, 0, 2, 3 };
    private static readonly int[] _quadOrderFlipped = { 1, 2, 3, 1, 3, 0 };

    /// <summary>
    /// Vertices come out in body voxel coordinates, so every sub-chunk of a body is drawn with
    /// the same model matrix.
    /// </summary>
    public static ChunkMeshData Build(VoxelBody body, int originX, int originY, int originZ)
    {
        var vertices = new List<float>(4096);
        var normals = new List<float>(4096);
        var colors = new List<byte>(8192);

        int end = VoxelBody.ChunkSize;

        for (int ly = 0; ly < end; ly++)
        for (int lz = 0; lz < end; lz++)
        for (int lx = 0; lx < end; lx++)
        {
            int x = originX + lx;
            int y = originY + ly;
            int z = originZ + lz;

            int id = body.Get(x, y, z);
            if (!BlockRegistry.IsSolid(id)) continue;

            BlockDef def = BlockRegistry.Get(id);
            Color albedo = TerrainColors.ForBlock(id, x, y, z);
            byte emissive = (byte)(Math.Clamp(def.Emissive, 0f, 1f) * 255f);

            for (int f = 0; f < _faces.Length; f++)
            {
                ref readonly Face face = ref _faces[f];
                if (BlockRegistry.IsSolid(body.Get(x + face.Nx, y + face.Ny, z + face.Nz))) continue;

                EmitFace(vertices, normals, colors, body, in face, x, y, z, albedo, emissive);
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

    private static void EmitFace(
        List<float> vertices,
        List<float> normals,
        List<byte> colors,
        VoxelBody body,
        in Face face,
        int x, int y, int z,
        Color albedo,
        byte emissive)
    {
        // The air cell in front of the face; the AO neighbours are sampled from there
        int airX = x + face.Nx;
        int airY = y + face.Ny;
        int airZ = z + face.Nz;

        Span<int> ambientOcclusion = stackalloc int[4];

        for (int i = 0; i < 4; i++)
        {
            (int cx, int cy, int cz) = face.Corners[i];

            // Signs of the tangents towards this corner
            int signU = (cx * face.Ux + cy * face.Uy + cz * face.Uz) == 1 ? 1 : -1;
            int signV = (cx * face.Vx + cy * face.Vy + cz * face.Vz) == 1 ? 1 : -1;

            bool side1 = BlockRegistry.IsSolid(body.Get(
                airX + signU * face.Ux, airY + signU * face.Uy, airZ + signU * face.Uz));
            bool side2 = BlockRegistry.IsSolid(body.Get(
                airX + signV * face.Vx, airY + signV * face.Vy, airZ + signV * face.Vz));
            bool corner = BlockRegistry.IsSolid(body.Get(
                airX + signU * face.Ux + signV * face.Vx,
                airY + signU * face.Uy + signV * face.Vy,
                airZ + signU * face.Uz + signV * face.Vz));

            ambientOcclusion[i] = (side1 && side2)
                ? 0
                : 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
        }

        // Pick the diagonal by AO, otherwise the quad shows an anisotropy artefact
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

            float light = 0.55f + 0.45f * (ambientOcclusion[cornerIndex] / 3f);
            colors.Add((byte)(albedo.R * light));
            colors.Add((byte)(albedo.G * light));
            colors.Add((byte)(albedo.B * light));
            colors.Add(emissive);
        }
    }
}
