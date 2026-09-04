using System.Numerics;

namespace VoxelEngine.World;

/// <summary>
/// One column of the world: <see cref="Size"/> blocks across and the world's height tall. The
/// column is cut into slabs of <see cref="SlabHeight"/> rows, and a slab of nothing but one block
/// (the sky, the deep rock) holds no array at all. A tall world is mostly such slabs, which is
/// what keeps a 256-block-high world at the memory of a much lower one.
///
/// The flat layout every generator and save file uses is x + Size * (z + Size * y), so a slab is
/// a contiguous run of that array and can be split off without re-indexing.
/// </summary>
public class Chunk
{
    public const int Size = 32;

    /// <summary>Rows per slab; the world height has to be a multiple of it</summary>
    public const int SlabHeight = 32;

    private const int SlabBytes = Size * SlabHeight * Size;

    // One entry per slab: the array, or null when the slab is all one block (_uniform says which)
    private readonly byte[]?[] _slabs;
    private readonly byte[] _uniform;

    // Sculpt and Smooth mode: one sub-voxel density field per edited block (key = flat index).
    // Invariants: air has no entry, and a full field is not stored (it is a full block).
    // The fields are copy-on-write: never change them in place, worker threads read them.
    private readonly Dictionary<int, byte[]> _refinements;

    public readonly ChunkCoord Coord;
    public readonly Vector3 WorldPosition;

    /// <summary>Rows in this column, the world's height</summary>
    public int Height { get; }

    /// <summary>True once the chunk changed since it was generated or loaded, so it needs saving</summary>
    public bool Modified { get; private set; }

    /// <summary>
    /// All eight neighbours are loaded. Only then can the chunk be meshed for good: its border
    /// faces and ambient occlusion read one block into the neighbours, and meshing it earlier
    /// would show a cliff wall at the frontier that has to be thrown away a moment later.
    /// </summary>
    internal bool Surrounded { get; set; }

    /// <summary>The first full-column mesh was requested; edits keep it up to date from there on</summary>
    internal bool MeshRequested { get; set; }

    public Chunk(ChunkCoord coord, int worldHeight, ITerrainGenerator generator)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);
        Height = CheckHeight(worldHeight);
        _refinements = new Dictionary<int, byte[]>();

        var blocks = new byte[Size * worldHeight * Size];
        generator.Generate(coord, blocks, worldHeight);

        _slabs = new byte[]?[worldHeight / SlabHeight];
        _uniform = new byte[_slabs.Length];
        Split(blocks);
    }

    /// <summary>Chunk from a save: the terrain comes from the file, not from the generator</summary>
    public Chunk(ChunkCoord coord, byte[] loadedBlocks, Dictionary<int, byte[]> loadedRefinements)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);
        Height = CheckHeight(loadedBlocks.Length / (Size * Size));
        _refinements = loadedRefinements;

        _slabs = new byte[]?[Height / SlabHeight];
        _uniform = new byte[_slabs.Length];
        Split(loadedBlocks);
    }

    private static int CheckHeight(int worldHeight)
    {
        if (worldHeight <= 0 || worldHeight % SlabHeight != 0)
            throw new ArgumentException($"World height has to be a positive multiple of {SlabHeight}", nameof(worldHeight));

        return worldHeight;
    }

    /// <summary>Takes the slabs out of a flat column; a slab of one block value keeps only that value</summary>
    private void Split(byte[] blocks)
    {
        for (int slab = 0; slab < _slabs.Length; slab++)
        {
            ReadOnlySpan<byte> run = blocks.AsSpan(slab * SlabBytes, SlabBytes);

            byte first = run[0];
            if (run.IndexOfAnyExcept(first) < 0)
            {
                _slabs[slab] = null;
                _uniform[slab] = first;
                continue;
            }

            _slabs[slab] = run.ToArray();
        }
    }

    /// <summary>The whole column as one flat array, for saving; allocated fresh each call</summary>
    internal byte[] ToFlatArray()
    {
        var blocks = new byte[Size * Height * Size];

        for (int slab = 0; slab < _slabs.Length; slab++)
        {
            byte[]? data = _slabs[slab];
            if (data != null) data.CopyTo(blocks, slab * SlabBytes);
            else if (_uniform[slab] != 0) blocks.AsSpan(slab * SlabBytes, SlabBytes).Fill(_uniform[slab]);
        }

        return blocks;
    }

    /// <summary>Slabs that hold an array rather than one value; what the column actually costs</summary>
    internal int AllocatedSlabs
    {
        get
        {
            int count = 0;
            foreach (byte[]? slab in _slabs)
                if (slab != null) count++;
            return count;
        }
    }

    internal void MarkSaved() => Modified = false;

    public int GetLocal(int x, int y, int z, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return 0;

        byte[]? slab = _slabs[y / SlabHeight];
        return slab == null ? _uniform[y / SlabHeight] : slab[SlabIndex(x, y, z)];
    }

    public void SetLocal(int x, int y, int z, int id, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return;

        byte value = (byte)Math.Clamp(id, 0, 255);
        int slab = y / SlabHeight;

        if (_slabs[slab] == null)
        {
            if (_uniform[slab] == value && !_refinements.ContainsKey(Index(x, y, z)))
                return; // already that block, nothing to allocate for

            Materialize(slab);
        }

        _slabs[slab]![SlabIndex(x, y, z)] = value;
        _refinements.Remove(Index(x, y, z)); // changing the block discards its density field
        Modified = true;
    }

    private void Materialize(int slab)
    {
        var data = new byte[SlabBytes];
        if (_uniform[slab] != 0) Array.Fill(data, _uniform[slab]);
        _slabs[slab] = data;
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

    internal int RefinementCount => _refinements.Count;

    internal static (int X, int Y, int Z) DecodeIndex(int index)
    {
        int x = index % Size;
        int z = (index / Size) % Size;
        int y = index / (Size * Size);
        return (x, y, z);
    }

    // Copies a whole X row in one go (for the mesh snapshot)
    public void CopyRow(int y, int z, byte[] destination, int destinationIndex)
    {
        byte[]? slab = _slabs[y / SlabHeight];

        if (slab == null) Array.Fill(destination, _uniform[y / SlabHeight], destinationIndex, Size);
        else Array.Copy(slab, SlabIndex(0, y, z), destination, destinationIndex, Size);
    }

    /// <summary>Copies the Z run of blocks at one x into a strided destination (the east and west shells of a snapshot)</summary>
    public void CopyColumn(int y, int x, byte[] destination, int destinationIndex, int destinationStride)
    {
        byte[]? slab = _slabs[y / SlabHeight];

        if (slab == null)
        {
            byte value = _uniform[y / SlabHeight];
            for (int z = 0; z < Size; z++)
                destination[destinationIndex + z * destinationStride] = value;
            return;
        }

        int source = SlabIndex(x, y, 0);
        for (int z = 0; z < Size; z++)
            destination[destinationIndex + z * destinationStride] = slab[source + z * Size];
    }

    private static bool InBounds(int x, int y, int z, int worldHeight)
        => x >= 0 && x < Size &&
           z >= 0 && z < Size &&
           y >= 0 && y < worldHeight;

    /// <summary>Position in the flat column layout, the one generators and refinement keys use</summary>
    public static int Index(int x, int y, int z)
        => x + Size * (z + Size * y);

    /// <summary>Position inside the slab that holds row y</summary>
    private static int SlabIndex(int x, int y, int z)
        => x + Size * (z + Size * (y % SlabHeight));
}
