namespace VoxelEngine.Rendering;

// The pure CPU result of meshing: holds no raylib resources and is therefore thread-safe
public sealed class ChunkMeshData
{
    public required float[] Vertices { get; init; }   // 3 floats per vertex, chunk-local coordinates
    public required float[] Normals { get; init; }    // 3 floats per vertex
    public required byte[] Colors { get; init; }      // RGBA per vertex; alpha carries emissive
    public required int VertexCount { get; init; }
}
