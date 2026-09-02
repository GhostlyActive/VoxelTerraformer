using System.Numerics;

namespace VoxelEngine.World;

public class Chunk
{
    public const int Size = 32;

    // Layout: x + Size * (z + Size * y)
    private readonly byte[] _blocks;

    // Sculpt and Smooth mode: one sub-voxel density field per edited block (key = index).
    // Invariants: air has no entry, and a full field is not stored (it is a full block).
    // The fields are copy-on-write: never change them in place, worker threads read them.
    private readonly Dictionary<int, byte[]> _refinements;

    public readonly ChunkCoord Coord;
    public readonly Vector3 WorldPosition;

    /// <summary>True once the chunk changed since it was generated or loaded, so it needs saving</summary>
    public bool Modified { get; private set; }

    public Chunk(ChunkCoord coord, int worldHeight, ITerrainGenerator generator)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);
        _refinements = new Dictionary<int, byte[]>();
        _blocks = new byte[Size * worldHeight * Size];

        generator.Generate(coord, _blocks, worldHeight);
    }

    /// <summary>Chunk from a save: the terrain comes from the file, not from the generator</summary>
    public Chunk(ChunkCoord coord, byte[] loadedBlocks, Dictionary<int, byte[]> loadedRefinements)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);
        _blocks = loadedBlocks;
        _refinements = loadedRefinements;
    }

    // Direct access for saving and loading only; do not use from gameplay code
    internal byte[] RawBlocks => _blocks;

    internal void MarkSaved() => Modified = false;

    public int GetLocal(int x, int y, int z, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return 0;
        return _blocks[Index(x, y, z)];
    }

    public void SetLocal(int x, int y, int z, int id, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return;
        int index = Index(x, y, z);
        _blocks[index] = (byte)Math.Clamp(id, 0, 255);
        _refinements.Remove(index); // changing the block discards its density field
        Modified = true;
    }

    public bool TryGetRefinement(int x, int y, int z, int worldHeight, out byte[] field)
    {
        field = null!;
        if (!InBounds(x, y, z, worldHeight)) return false;
        return _refinements.TryGetValue(Index(x, y, z), out field!);
    }

    public void SetRefinement(int x, int y, int z, byte[] field, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return;
        int index = Index(x, y, z);

        if (SubVoxels.IsEmpty(field) || SubVoxels.IsFull(field)) _refinements.Remove(index);
        else _refinements[index] = field;

        Modified = true;
    }

    internal IReadOnlyDictionary<int, byte[]> Refinements => _refinements;

    internal static (int X, int Y, int Z) DecodeIndex(int index)
    {
        int x = index % Size;
        int z = (index / Size) % Size;
        int y = index / (Size * Size);
        return (x, y, z);
    }

    // Copies a whole X row in one go (for the mesh snapshot)
    public void CopyRow(int y, int z, byte[] destination, int destinationIndex)
        => Array.Copy(_blocks, Index(0, y, z), destination, destinationIndex, Size);

    private static bool InBounds(int x, int y, int z, int worldHeight)
        => x >= 0 && x < Size &&
           z >= 0 && z < Size &&
           y >= 0 && y < worldHeight;

    public static int Index(int x, int y, int z)
        => x + Size * (z + Size * y);
}
