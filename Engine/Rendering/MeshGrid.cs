using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// The block grid a mesher reads: a chunk's blocks with a one-block border on every side for face
/// culling and ambient occlusion across chunk borders. At full detail that is the chunk itself;
/// for a distant level of detail it is the chunk downsampled, where one grid block stands for
/// <see cref="Scale"/> metres. Layout: x + Stride * (z + Stride * y), with the border at index 0.
/// </summary>
public readonly struct MeshGrid
{
    public readonly byte[] Blocks;

    /// <summary>Interior blocks per horizontal axis (32 at full detail, 16 at 2 m, 8 at 4 m)</summary>
    public readonly int Size;

    /// <summary>Interior rows</summary>
    public readonly int Height;

    /// <summary>Metres per grid block</summary>
    public readonly int Scale;

    public MeshGrid(byte[] blocks, int size, int height, int scale)
    {
        Blocks = blocks;
        Size = size;
        Height = height;
        Scale = scale;
    }

    public int Stride => Size + 2;

    public int Length => LengthFor(Size, Height);

    public static int LengthFor(int size, int height) => (size + 2) * (size + 2) * (height + 2);

    /// <summary>Takes local coordinates -1..Size, or -1..Height on Y</summary>
    public int Index(int x, int y, int z) => (x + 1) + Stride * ((z + 1) + Stride * (y + 1));

    /// <summary>The full-detail layout of a chunk column, the one refinement keys refer to</summary>
    public static MeshGrid FullDetail(byte[] padded, int worldHeight) => new(padded, Chunk.Size, worldHeight, 1);

    /// <summary>The layout of a chunk column at a coarser level: <paramref name="scale"/> metres per block</summary>
    public static MeshGrid Coarse(byte[] blocks, int worldHeight, int scale)
        => new(blocks, Chunk.Size / scale, worldHeight / scale, scale);
}
