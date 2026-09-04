namespace VoxelEngine.Rendering;

/// <summary>
/// The pure CPU result of meshing one part: packed vertices and a triangle list of 16-bit
/// indices. Holds no raylib resources and is therefore thread-safe; <see cref="GpuMesh"/> turns it
/// into buffers on the main thread.
/// </summary>
public sealed class ChunkMeshData
{
    public required TerrainVertex[] Vertices { get; init; }

    public required ushort[] Indices { get; init; }

    public required int VertexCount { get; init; }

    public required int IndexCount { get; init; }

    public int TriangleCount => IndexCount / 3;
}
