using Raylib_cs;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GPU side of one mesh part: a vertex array with one interleaved <see cref="TerrainVertex"/>
/// buffer and a 16-bit index buffer, created straight through rlgl. Nothing is kept on the CPU
/// once the data is uploaded, where raylib's own meshes hold a second copy of every vertex for
/// as long as the mesh lives.
///
/// Attribute slots follow raylib's convention, which is how the terrain shader's vertexPosition,
/// vertexNormal and vertexColor find the data: 0 for the position, 2 for the normal, 3 for the colour.
/// </summary>
public sealed class GpuMesh : IDisposable
{
    private const int GlFloat = 0x1406;
    private const int GlByte = 0x1400;
    private const int GlUnsignedByte = 0x1401;

    private const uint PositionSlot = 0;
    private const uint NormalSlot = 2;
    private const uint ColorSlot = 3;

    private uint _vertexArray;
    private uint _vertexBuffer;
    private uint _indexBuffer;

    public int VertexCount { get; }
    public int IndexCount { get; }
    public int TriangleCount => IndexCount / 3;

    /// <summary>Bytes the buffers take on the GPU</summary>
    public int Bytes => VertexCount * TerrainVertex.Size + IndexCount * sizeof(ushort);

    public unsafe GpuMesh(ReadOnlySpan<TerrainVertex> vertices, ReadOnlySpan<ushort> indices)
    {
        VertexCount = vertices.Length;
        IndexCount = indices.Length;

        _vertexArray = Rlgl.LoadVertexArray();
        Rlgl.EnableVertexArray(_vertexArray);

        fixed (TerrainVertex* data = vertices)
            _vertexBuffer = Rlgl.LoadVertexBuffer(data, vertices.Length * TerrainVertex.Size, false);

        Rlgl.SetVertexAttribute(PositionSlot, 3, GlFloat, false, TerrainVertex.Size, 0);
        Rlgl.EnableVertexAttribute(PositionSlot);

        Rlgl.SetVertexAttribute(NormalSlot, 3, GlByte, true, TerrainVertex.Size, 12);
        Rlgl.EnableVertexAttribute(NormalSlot);

        Rlgl.SetVertexAttribute(ColorSlot, 4, GlUnsignedByte, true, TerrainVertex.Size, 16);
        Rlgl.EnableVertexAttribute(ColorSlot);

        fixed (ushort* data = indices)
            _indexBuffer = Rlgl.LoadVertexBufferElement(data, indices.Length * sizeof(ushort), false);

        Rlgl.DisableVertexArray();
    }

    /// <summary>Issues the draw; the caller has bound the shader and set its matrices (<see cref="MeshDrawer"/>)</summary>
    public unsafe void Draw()
    {
        Rlgl.EnableVertexArray(_vertexArray);
        Rlgl.DrawVertexArrayElements(0, IndexCount, null);
    }

    public void Dispose()
    {
        if (_vertexArray == 0) return;

        Rlgl.UnloadVertexBuffer(_vertexBuffer);
        Rlgl.UnloadVertexBuffer(_indexBuffer);
        Rlgl.UnloadVertexArray(_vertexArray);

        _vertexArray = 0;
    }
}
