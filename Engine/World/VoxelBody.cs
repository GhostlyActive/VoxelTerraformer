using System.Numerics;
using VoxelEngine.MathTools;

namespace VoxelEngine.World;

/// <summary>
/// A free-standing voxel body: a planet, a moon, an asteroid. Unlike the streamed
/// <see cref="VoxelWorld"/> it has a fixed side length, no neighbours and a place of its own in
/// space: position, the size of a single voxel, and a spin around the Y axis.
///
/// The side length is deliberately <see cref="Chunk.Size"/>, which makes it possible to reuse the
/// same <c>ChunkMesher</c> the terrain runs through, ambient occlusion included. Bigger bodies come
/// from <see cref="VoxelScale"/> rather than from more voxels.
/// </summary>
public sealed class VoxelBody
{
    public const int Size = Chunk.Size;

    private static readonly Vector3 GridCenter = new(Size * 0.5f);

    private readonly byte[] _blocks = new byte[Size * Size * Size];

    public required string Name { get; init; }

    /// <summary>Centre of the body in world coordinates</summary>
    public Vector3 Position { get; set; }

    /// <summary>Side length of one voxel in metres</summary>
    public float VoxelScale { get; init; } = 1f;

    /// <summary>Own rotation around the Y axis, in radians</summary>
    public float Spin { get; set; }

    public float SpinSpeed { get; init; }

    /// <summary>Radius of the bounding sphere, for coarse hit tests and collision</summary>
    public float BoundingRadius => Size * 0.5f * MathF.Sqrt(3f) * VoxelScale;

    /// <summary>The mesh needs rebuilding</summary>
    public bool Dirty { get; private set; } = true;

    public void MarkClean() => Dirty = false;

    public void Advance(float dt) => Spin = (Spin + SpinSpeed * dt) % MathF.Tau;

    public int Get(int x, int y, int z)
    {
        if (!InBounds(x, y, z)) return BlockRegistry.Air;
        return _blocks[Index(x, y, z)];
    }

    public void Set(int x, int y, int z, byte id)
    {
        if (!InBounds(x, y, z)) return;

        int index = Index(x, y, z);
        if (_blocks[index] == id) return;

        _blocks[index] = id;
        Dirty = true;
    }

    /// <summary>A world point in the body's voxel coordinates (0..Size)</summary>
    public Vector3 ToLocal(Vector3 worldPoint)
        => RotateY(worldPoint - Position, -Spin) / VoxelScale + GridCenter;

    /// <summary>Translate a world direction into the body's rotation, sunlight for instance</summary>
    public Vector3 DirectionToLocal(Vector3 worldDirection) => RotateY(worldDirection, -Spin);

    public bool IsSolidAt(Vector3 worldPoint)
    {
        Vector3 local = ToLocal(worldPoint);

        return BlockRegistry.IsSolid(Get(
            (int)MathF.Floor(local.X),
            (int)MathF.Floor(local.Y),
            (int)MathF.Floor(local.Z)));
    }

    /// <summary>
    /// Blast a sphere out of the body. Returns how many voxels were removed; 0 means the hit
    /// landed beside it and the caller can discard it.
    /// </summary>
    public int Carve(Vector3 worldCenter, float worldRadius)
    {
        Vector3 center = ToLocal(worldCenter);
        float radius = worldRadius / VoxelScale;

        int minX = Math.Max(0, (int)MathF.Floor(center.X - radius));
        int maxX = Math.Min(Size - 1, (int)MathF.Ceiling(center.X + radius));
        int minY = Math.Max(0, (int)MathF.Floor(center.Y - radius));
        int maxY = Math.Min(Size - 1, (int)MathF.Ceiling(center.Y + radius));
        int minZ = Math.Max(0, (int)MathF.Floor(center.Z - radius));
        int maxZ = Math.Min(Size - 1, (int)MathF.Ceiling(center.Z + radius));

        int removed = 0;

        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            int index = Index(x, y, z);
            if (!BlockRegistry.IsSolid(_blocks[index])) continue;

            var voxelCenter = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if (Vector3.DistanceSquared(voxelCenter, center) > radius * radius) continue;

            _blocks[index] = BlockRegistry.Air;
            removed++;
        }

        if (removed > 0) Dirty = true;
        return removed;
    }

    public int CountSolid()
    {
        int solid = 0;
        foreach (byte block in _blocks)
            if (BlockRegistry.IsSolid(block)) solid++;

        return solid;
    }

    /// <summary>
    /// Fill the body as a sphere. <paramref name="roughness"/> ruffles the surface with 3D noise,
    /// <paramref name="coreDepth"/> is the thickness of the crust in voxels; below it the core
    /// material shows through as soon as something is taken off.
    /// </summary>
    public void FillSphere(float radius, byte crust, byte core, int seed, float roughness = 0.12f, int coreDepth = 4)
    {
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            var voxel = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float distance = Vector3.Distance(voxel, GridCenter);

            float bumps = Noise.Fbm3D(x * 0.16f, y * 0.16f, z * 0.16f, seed, 3, 0.5f, 2f) - 0.5f;
            float surface = radius * (1f + bumps * roughness * 2f);

            if (distance > surface) continue;

            _blocks[Index(x, y, z)] = distance > surface - coreDepth ? crust : core;
        }

        Dirty = true;
    }

    private static Vector3 RotateY(Vector3 v, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        return new Vector3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
    }

    private static bool InBounds(int x, int y, int z)
        => x >= 0 && x < Size && y >= 0 && y < Size && z >= 0 && z < Size;

    private static int Index(int x, int y, int z) => x + Size * (z + Size * y);
}
