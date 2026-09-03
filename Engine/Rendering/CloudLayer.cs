using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// A cloud layer made of voxels: every occupied cell of the sky grid becomes a lump of cubes
/// whose shape comes from an ellipsoid roughened with hash noise, which gives rounded, irregular
/// clouds instead of visible tiles. The layer drifts east and wraps cell by cell without a seam.
///
/// The lumps are built once into a pool of meshes and then drawn one per cell. Emitting them as
/// separate cubes every frame cost more than the entire voxel terrain: a lump is around fifty
/// cubes, and a sky this wide holds a few hundred cells the player can see. Out of the pool a shape
/// comes back around every so often, which at this distance reads as clouds of a kind rather than
/// as a repeating pattern.
/// </summary>
public sealed class CloudLayer : IDisposable
{
    // Cell and cube size scale with the range, so a wider sky costs the same number of blocks and
    // the clouds keep roughly the same size on screen
    private const float CellSize = 46f;
    private const float CubeSize = 7f;       // voxel size of the clouds
    private const int BlobX = 6;             // cubes per axis in a lump
    private const int BlobY = 3;
    private const int BlobZ = 6;
    private const float Range = 780f;        // out past the fog, and it travels with the player

    /// <summary>Distinct lump shapes to pick from</summary>
    private const int ShapeCount = 24;

    public float Coverage { get; set; } = 0.35f;
    public float Height { get; set; } = 80f;
    public float DriftSpeed { get; set; } = 1.2f;   // blocks per second

    private Mesh[]? _shapes;
    private Material _material;

    /// <summary>
    /// <paramref name="frustum"/> is the same one the terrain is culled against. Without it the
    /// layer draws every cell of a sky nearly two kilometres across, of which the player sees
    /// maybe a quarter.
    /// </summary>
    public void Draw(Frustum frustum, float time, float daylight01, Vector3 center)
    {
        // The meshes need a GL context, which the constructor cannot count on
        _shapes ??= BuildShapes();

        // nearly white by day, dark blue-grey at night
        SetTint(Lerp(new Color(88, 98, 126, 255), new Color(250, 250, 255, 255), daylight01));

        float offset = time * DriftSpeed / CellSize;
        int shift = (int)MathF.Floor(offset);
        float slide = (offset - shift) * CellSize;

        // Half the extent of a lump, for the box the frustum is tested against
        var blobExtent = new Vector3(BlobX * CubeSize * 0.7f, BlobY * CubeSize * 0.7f, BlobZ * CubeSize * 0.7f);

        int minX = (int)MathF.Floor((center.X - Range) / CellSize) - 1;
        int maxX = (int)MathF.Floor((center.X + Range) / CellSize);
        int minZ = (int)MathF.Floor((center.Z - Range) / CellSize);
        int maxZ = (int)MathF.Floor((center.Z + Range) / CellSize);

        for (int cz = minZ; cz <= maxZ; cz++)
        for (int cx = minX; cx <= maxX; cx++)
        {
            if (Hash(cx - shift, cz, 0) > Coverage) continue;

            var cellCenter = new Vector3(
                cx * CellSize + CellSize / 2f + slide,
                Height,
                cz * CellSize + CellSize / 2f);

            if (!frustum.Intersects(cellCenter - blobExtent, cellCenter + blobExtent)) continue;

            // Keyed on the drifting cell, so a cloud keeps its shape while it travels
            int shape = Math.Min(ShapeCount - 1, (int)(Hash(cx - shift, cz, 5) * ShapeCount));

            Raylib.DrawMesh(_shapes[shape], _material,
                Raymath.MatrixTranslate(cellCenter.X, cellCenter.Y, cellCenter.Z));
        }
    }

    private Mesh[] BuildShapes()
    {
        _material = Raylib.LoadMaterialDefault();

        var shapes = new Mesh[ShapeCount];
        for (int i = 0; i < ShapeCount; i++) shapes[i] = BuildShape(i);

        return shapes;
    }

    /// <summary>
    /// One lump: cubes inside an ellipsoid whose edge is frayed with a hash. Only the faces that
    /// end up on the outside are kept — a lump is solid enough that most of them face a neighbour.
    /// </summary>
    private static Mesh BuildShape(int shape)
    {
        var filled = new bool[BlobX * BlobY * BlobZ];

        // Every cloud gets its own proportions, or the shape repeats too obviously
        float stretchX = 0.75f + Hash(shape, 0, 91) * 0.55f;
        float stretchZ = 0.75f + Hash(shape, 0, 92) * 0.55f;

        for (int iy = 0; iy < BlobY; iy++)
        for (int iz = 0; iz < BlobZ; iz++)
        for (int ix = 0; ix < BlobX; ix++)
        {
            // -1..1 relative to the centre of the lump
            float nx = (ix - (BlobX - 1) / 2f) / (BlobX / 2f) / stretchX;
            float ny = (iy - (BlobY - 1) / 2f) / (BlobY / 2f);
            float nz = (iz - (BlobZ - 1) / 2f) / (BlobZ / 2f);

            // The underside is flatter than the top, so clouds sit on a base
            float squash = ny < 0f ? 1.5f : 1f;
            float distance = nx * nx + (ny * squash) * (ny * squash) + nz * nz;

            float threshold = 0.75f + Hash(shape, 0, ix + BlobX * (iy + BlobY * iz) + 7) * 0.45f;
            filled[CubeIndex(ix, iy, iz)] = distance <= threshold;
        }

        var vertices = new List<float>(2048);
        var normals = new List<float>(2048);

        for (int iy = 0; iy < BlobY; iy++)
        for (int iz = 0; iz < BlobZ; iz++)
        for (int ix = 0; ix < BlobX; ix++)
        {
            if (!filled[CubeIndex(ix, iy, iz)]) continue;

            float baseX = (ix - (BlobX - 1) / 2f) * CubeSize;
            float baseY = (iy - (BlobY - 1) / 2f) * CubeSize;
            float baseZ = (iz - (BlobZ - 1) / 2f) * CubeSize;

            foreach ((int nx, int ny, int nz, (int X, int Y, int Z)[] corners) in _faces)
            {
                if (IsFilled(filled, ix + nx, iy + ny, iz + nz)) continue;

                foreach (int corner in _quadOrder)
                {
                    (int cx, int cy, int cz) = corners[corner];

                    vertices.Add(baseX + (cx - 0.5f) * CubeSize);
                    vertices.Add(baseY + (cy - 0.5f) * CubeSize);
                    vertices.Add(baseZ + (cz - 0.5f) * CubeSize);

                    normals.Add(nx);
                    normals.Add(ny);
                    normals.Add(nz);
                }
            }
        }

        int vertexCount = vertices.Count / 3;
        var mesh = new Mesh(vertexCount, vertexCount / 3);
        mesh.AllocVertices();
        mesh.AllocNormals();

        vertices.CopyTo(mesh.VerticesAs<float>());
        normals.CopyTo(mesh.NormalsAs<float>());
        Raylib.UploadMesh(ref mesh, false);

        return mesh;
    }

    /// <summary>The default material multiplies its albedo colour into every vertex</summary>
    private unsafe void SetTint(Color color) => _material.Maps[(int)MaterialMapIndex.Albedo].Color = color;

    private static int CubeIndex(int x, int y, int z) => x + BlobX * (z + BlobZ * y);

    private static bool IsFilled(bool[] filled, int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < BlobX && y < BlobY && z < BlobZ && filled[CubeIndex(x, y, z)];

    // 4 corners each, counter-clockwise seen from outside
    private static readonly (int Nx, int Ny, int Nz, (int X, int Y, int Z)[] Corners)[] _faces =
    {
        (0, 1, 0, new[] { (0, 1, 1), (1, 1, 1), (1, 1, 0), (0, 1, 0) }),
        (0, -1, 0, new[] { (0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1) }),
        (1, 0, 0, new[] { (1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1) }),
        (-1, 0, 0, new[] { (0, 0, 1), (0, 1, 1), (0, 1, 0), (0, 0, 0) }),
        (0, 0, 1, new[] { (1, 0, 1), (1, 1, 1), (0, 1, 1), (0, 0, 1) }),
        (0, 0, -1, new[] { (0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0) }),
    };

    private static readonly int[] _quadOrder = { 0, 1, 2, 0, 2, 3 };

    private static float Hash(int x, int z, int salt)
    {
        unchecked
        {
            int h = x * 374761393 + z * 668265263 + salt * unchecked((int)2246822519);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x7fffffff) / 2147483647f;
        }
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    public void Dispose()
    {
        if (_shapes == null) return;

        foreach (Mesh shape in _shapes) Raylib.UnloadMesh(shape);
        Raylib.UnloadMaterial(_material);

        _shapes = null;
    }
}
