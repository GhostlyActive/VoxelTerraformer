using System.Numerics;
using VoxelEngine.MathTools;

namespace VoxelEngine.World;

/// <summary>
/// Ein freistehender Voxel-Körper — Planet, Mond, Asteroid. Anders als die gestreamte
/// <see cref="VoxelWorld"/> hat er eine feste Kantenlänge, keine Nachbarn und eine eigene
/// Lage im Raum: Position, Kantenlänge eines Voxels und eine Drehung um die Y-Achse.
///
/// Die Kantenlänge ist bewusst <see cref="Chunk.Size"/> — damit lässt sich derselbe
/// <c>ChunkMesher</c> verwenden, den auch das Gelände benutzt, samt Ambient Occlusion.
/// Größere Körper entstehen nicht über mehr Voxel, sondern über <see cref="VoxelScale"/>.
/// </summary>
public sealed class VoxelBody
{
    public const int Size = Chunk.Size;

    private static readonly Vector3 GridCenter = new(Size * 0.5f);

    private readonly byte[] _blocks = new byte[Size * Size * Size];

    public required string Name { get; init; }

    /// <summary>Mittelpunkt des Körpers in Weltkoordinaten</summary>
    public Vector3 Position { get; set; }

    /// <summary>Kantenlänge eines Voxels in Metern</summary>
    public float VoxelScale { get; init; } = 1f;

    /// <summary>Eigendrehung um die Y-Achse in Radiant</summary>
    public float Spin { get; set; }

    public float SpinSpeed { get; init; }

    /// <summary>Radius der Hüllkugel — für grobe Trefferabfragen und Kollision</summary>
    public float BoundingRadius => Size * 0.5f * MathF.Sqrt(3f) * VoxelScale;

    /// <summary>Mesh muss neu gebaut werden</summary>
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

    /// <summary>Weltpunkt in Voxelkoordinaten des Körpers (0..Size)</summary>
    public Vector3 ToLocal(Vector3 worldPoint)
        => RotateY(worldPoint - Position, -Spin) / VoxelScale + GridCenter;

    /// <summary>Richtung aus der Welt in die Körperdrehung übersetzen (z. B. Sonnenlicht)</summary>
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
    /// Kugel aus dem Körper herausschlagen. Liefert die Anzahl entfernter Voxel — 0 heißt,
    /// der Einschlag lag daneben und der Aufrufer kann den Treffer verwerfen.
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
    /// Kugelförmigen Körper füllen. <paramref name="roughness"/> wellt die Oberfläche über
    /// 3D-Rauschen auf, <paramref name="coreDepth"/> ist die Dicke der Kruste in Voxeln —
    /// darunter kommt das Kernmaterial zum Vorschein, sobald etwas abgetragen wird.
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
