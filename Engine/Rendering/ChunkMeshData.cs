namespace VoxelEngine.Rendering;

// Reines CPU-Ergebnis des Meshings — enthält keine Raylib-Ressourcen und ist damit Thread-sicher
public sealed class ChunkMeshData
{
    public required float[] Vertices { get; init; }   // 3 floats pro Vertex, chunk-lokale Koordinaten
    public required float[] Normals { get; init; }    // 3 floats pro Vertex
    public required byte[] Colors { get; init; }      // RGBA pro Vertex, Alpha transportiert Emissive
    public required int VertexCount { get; init; }
}
