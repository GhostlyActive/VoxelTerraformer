using System.Numerics;
using VoxelEngine.MathTools;

namespace VoxelEngine.World;

/// <summary>
/// A free-standing voxel body: a planet, a moon, an asteroid. Unlike the streamed
/// <see cref="VoxelWorld"/> it has a fixed side length, no neighbours and a place of its own in
/// space: position, the size of a single voxel, and a spin around the Y axis.
///
/// The grid is split into cubes of <see cref="ChunkSize"/> so a hit only forces the few sub-chunks
/// it touched to be remeshed. Without that, a planet of a hundred voxels across would rebuild a
/// million voxels for every shot.
/// </summary>
/// <summary>Reports a voxel a carve took out: its centre in world space and the material that was there</summary>
public delegate void VoxelRemoved(Vector3 worldPosition, byte block);

public sealed class VoxelBody
{
    /// <summary>Side length of one meshing sub-chunk; <see cref="Size"/> has to be a multiple of it</summary>
    public const int ChunkSize = 32;

    private readonly byte[] _blocks;
    private readonly bool[] _chunkDirty;
    private readonly int _chunksPerAxis;

    private readonly Vector3 _gridCenter;

    public string Name { get; }

    /// <summary>Grid resolution: the body is Size³ voxels</summary>
    public int Size { get; }

    /// <summary>Side length of one voxel in metres</summary>
    public float VoxelScale { get; }

    /// <summary>Centre of the body in world coordinates</summary>
    public Vector3 Position { get; set; }

    /// <summary>Own rotation around the Y axis, in radians</summary>
    public float Spin { get; set; }

    public float SpinSpeed { get; init; }

    /// <summary>Radius of the filled sphere in metres, set by <see cref="FillSphere"/></summary>
    public float SurfaceRadius { get; private set; }

    /// <summary>Radius in metres below which only core material sits, set by <see cref="FillSphere"/></summary>
    public float CoreRadius { get; private set; }

    /// <summary>Radius of the bounding sphere, for coarse hit tests and collision</summary>
    public float BoundingRadius => Size * 0.5f * MathF.Sqrt(3f) * VoxelScale;

    public int ChunkCount => _chunkDirty.Length;

    public VoxelBody(string name, int size, float voxelScale, float spinSpeed = 0f)
    {
        if (size <= 0 || size % ChunkSize != 0)
            throw new ArgumentException($"Size has to be a positive multiple of {ChunkSize}", nameof(size));

        Name = name;
        Size = size;
        VoxelScale = voxelScale;
        SpinSpeed = spinSpeed;

        _blocks = new byte[size * size * size];
        _chunksPerAxis = size / ChunkSize;
        _chunkDirty = new bool[_chunksPerAxis * _chunksPerAxis * _chunksPerAxis];
        _gridCenter = new Vector3(size * 0.5f);

        Array.Fill(_chunkDirty, true);
    }

    public void Advance(float dt) => Spin = (Spin + SpinSpeed * dt) % MathF.Tau;

    public bool IsChunkDirty(int chunkIndex) => _chunkDirty[chunkIndex];

    public void MarkChunkClean(int chunkIndex) => _chunkDirty[chunkIndex] = false;

    /// <summary>Voxel coordinate of a sub-chunk's lower corner</summary>
    public (int X, int Y, int Z) ChunkOrigin(int chunkIndex)
    {
        int x = chunkIndex % _chunksPerAxis;
        int z = chunkIndex / _chunksPerAxis % _chunksPerAxis;
        int y = chunkIndex / (_chunksPerAxis * _chunksPerAxis);

        return (x * ChunkSize, y * ChunkSize, z * ChunkSize);
    }

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
        MarkDirtyAround(x, y, z, x, y, z);
    }

    /// <summary>A world point in the body's voxel coordinates (0..Size)</summary>
    public Vector3 ToLocal(Vector3 worldPoint)
        => RotateY(worldPoint - Position, -Spin) / VoxelScale + _gridCenter;

    /// <summary>A point in voxel coordinates back into the world</summary>
    public Vector3 ToWorld(Vector3 localPoint)
        => Position + RotateY((localPoint - _gridCenter) * VoxelScale, Spin);

    /// <summary>A direction from the body's frame into the world; only the spin applies</summary>
    public Vector3 DirectionToWorld(Vector3 localDirection) => RotateY(localDirection, Spin);

    /// <summary>A world direction into the body's frame, so it turns with the body</summary>
    public Vector3 DirectionToLocal(Vector3 worldDirection) => RotateY(worldDirection, -Spin);

    /// <summary>Straight up from the body's centre through a world point</summary>
    public Vector3 UpAt(Vector3 worldPoint)
    {
        Vector3 up = worldPoint - Position;
        return up.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(up);
    }

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
        => Carve(worldCenter, worldRadius, BlockRegistry.Air, out _);

    /// <summary>
    /// Same, but also counts how many voxels of <paramref name="watchMaterial"/> went with it —
    /// which is how a game notices that a shot has reached something that should not be touched.
    ///
    /// <paramref name="report"/> hands back every <paramref name="reportEvery"/>-th removed voxel,
    /// so a caller can turn the material it just destroyed into flying rubble instead of inventing
    /// debris out of nothing. Sampling matters: a planet-sized carve removes hundreds of thousands
    /// of voxels and nobody wants a chunk for each.
    /// </summary>
    public int Carve(Vector3 worldCenter, float worldRadius, byte watchMaterial, out int watchedRemoved,
        VoxelRemoved? report = null, int reportEvery = 1)
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
        watchedRemoved = 0;

        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            int index = Index(x, y, z);
            byte block = _blocks[index];
            if (!BlockRegistry.IsSolid(block)) continue;

            var voxelCenter = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if (Vector3.DistanceSquared(voxelCenter, center) > radius * radius) continue;

            _blocks[index] = BlockRegistry.Air;
            removed++;
            if (block == watchMaterial) watchedRemoved++;

            if (report != null && removed % reportEvery == 0) report(ToWorld(voxelCenter), block);
        }

        if (removed > 0) MarkDirtyAround(minX, minY, minZ, maxX, maxY, maxZ);
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
    /// <paramref name="crustDepth"/> is the thickness of the crust in voxels; below it the core
    /// material shows through as soon as something is taken off.
    /// </summary>
    public void FillSphere(float radiusVoxels, byte crust, byte core, int seed, float roughness = 0.12f, int crustDepth = 4)
    {
        SurfaceRadius = radiusVoxels * VoxelScale;
        CoreRadius = MathF.Max(0f, (radiusVoxels - crustDepth) * VoxelScale);

        // The noise frequency is tied to the grid, so a large body gets more surface detail rather
        // than the same handful of bumps stretched over it. Kept low enough that the result reads
        // as continents and basins instead of fine corrugation.
        float frequency = 3.2f / Size;
        float fineFrequency = frequency * 4.5f;

        // The bumps stay within this share of the radius (coarse and fine noise are both -0.5..0.5),
        // so anything deeper than that plus the crust is core and anything further out is air,
        // without asking the noise. On a planet of 250 voxels that is most of the volume.
        float bumpLimit = roughness * 2f * 0.57f;
        float deepestSurface = radiusVoxels * (1f - bumpLimit);
        float highestSurface = radiusVoxels * (1f + bumpLimit);

        Parallel.For(0, Size, y =>
        {
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                var voxel = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                float distance = Vector3.Distance(voxel, _gridCenter);

                if (distance > highestSurface) continue;

                if (distance <= deepestSurface - crustDepth)
                {
                    _blocks[Index(x, y, z)] = core;
                    continue;
                }

                // Two scales of noise: the coarse one shapes basins and ridges, the fine one breaks up
                // the concentric terraces a voxelised sphere would otherwise show as tree rings
                float coarse = Noise.Fbm3D(x * frequency, y * frequency, z * frequency, seed, 4, 0.5f, 2f) - 0.5f;
                float fine = Noise.Fbm3D(x * fineFrequency, y * fineFrequency, z * fineFrequency, seed + 777, 2, 0.5f, 2f) - 0.5f;

                float bumps = coarse * 0.78f + fine * 0.36f;
                float surface = radiusVoxels * (1f + bumps * roughness * 2f);

                if (distance > surface) continue;

                _blocks[Index(x, y, z)] = distance > surface - crustDepth ? crust : core;
            }
        });

        Array.Fill(_chunkDirty, true);
    }

    // A voxel on a chunk border also changes the faces of the neighbouring chunk, so the range is
    // widened by one before it is converted to chunk indices
    private void MarkDirtyAround(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        int cx0 = Math.Max(0, (minX - 1) / ChunkSize);
        int cy0 = Math.Max(0, (minY - 1) / ChunkSize);
        int cz0 = Math.Max(0, (minZ - 1) / ChunkSize);
        int cx1 = Math.Min(_chunksPerAxis - 1, (maxX + 1) / ChunkSize);
        int cy1 = Math.Min(_chunksPerAxis - 1, (maxY + 1) / ChunkSize);
        int cz1 = Math.Min(_chunksPerAxis - 1, (maxZ + 1) / ChunkSize);

        for (int cy = cy0; cy <= cy1; cy++)
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cx = cx0; cx <= cx1; cx++)
            _chunkDirty[cx + _chunksPerAxis * (cz + _chunksPerAxis * cy)] = true;
    }

    private static Vector3 RotateY(Vector3 v, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        return new Vector3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
    }

    private bool InBounds(int x, int y, int z)
        => x >= 0 && x < Size && y >= 0 && y < Size && z >= 0 && z < Size;

    private int Index(int x, int y, int z) => x + Size * (z + Size * y);
}
