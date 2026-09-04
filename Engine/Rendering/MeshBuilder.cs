namespace VoxelEngine.Rendering;

/// <summary>
/// Collects the triangles of one chunk section into GPU-ready arrays: packed vertices and 16-bit
/// indices. Vertices are shared through the index buffer, which is what keeps a smooth surface at
/// a fifth of the memory of one triangle soup.
///
/// raylib and the index buffer are 16-bit, so a mesh holds at most <see cref="MaxVerticesPerPart"/>
/// vertices. A section that needs more is cut into several parts; callers that cache vertex
/// indices have to drop their cache when <see cref="EnsureRoom"/> reports a cut, because the
/// indices start over.
///
/// One builder per worker thread; it keeps its arrays between sections.
/// </summary>
public sealed class MeshBuilder
{
    public const int MaxVerticesPerPart = 65535;

    private TerrainVertex[] _vertices = new TerrainVertex[8192];
    private ushort[] _indices = new ushort[3 * 16384];

    private int _vertexCount;
    private int _indexCount;

    private readonly List<ChunkMeshData> _parts = new();

    public int VertexCount => _vertexCount;

    public void Clear()
    {
        _vertexCount = 0;
        _indexCount = 0;
        _parts.Clear();
    }

    /// <summary>
    /// Makes sure the next <paramref name="vertices"/> vertices fit into the current part. Returns
    /// true when the part had to be closed and a new one begun: every vertex index handed out so
    /// far is then invalid.
    /// </summary>
    public bool EnsureRoom(int vertices)
    {
        if (_vertexCount + vertices <= MaxVerticesPerPart) return false;

        FlushPart();
        return true;
    }

    public int AddVertex(float x, float y, float z, float nx, float ny, float nz, byte r, byte g, byte b, byte emissive)
    {
        if (_vertexCount == _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);

        ref TerrainVertex vertex = ref _vertices[_vertexCount];
        vertex.X = x;
        vertex.Y = y;
        vertex.Z = z;
        vertex.NormalX = TerrainVertex.PackNormal(nx);
        vertex.NormalY = TerrainVertex.PackNormal(ny);
        vertex.NormalZ = TerrainVertex.PackNormal(nz);
        vertex.Padding = 0;
        vertex.R = r;
        vertex.G = g;
        vertex.B = b;
        vertex.Emissive = emissive;

        return _vertexCount++;
    }

    public void AddTriangle(int a, int b, int c)
    {
        if (_indexCount + 3 > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);

        _indices[_indexCount++] = (ushort)a;
        _indices[_indexCount++] = (ushort)b;
        _indices[_indexCount++] = (ushort)c;
    }

    /// <summary>Two triangles over four corners given counter-clockwise: (a,b,c) and (a,c,d)</summary>
    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    private void FlushPart()
    {
        if (_vertexCount == 0)
        {
            _indexCount = 0;
            return;
        }

        _parts.Add(new ChunkMeshData
        {
            Vertices = _vertices.AsSpan(0, _vertexCount).ToArray(),
            Indices = _indices.AsSpan(0, _indexCount).ToArray(),
            VertexCount = _vertexCount,
            IndexCount = _indexCount,
        });

        _vertexCount = 0;
        _indexCount = 0;
    }

    /// <summary>Closes the current part and hands back everything built since the last call</summary>
    public ChunkMeshData[] Finish()
    {
        FlushPart();

        ChunkMeshData[] result = _parts.Count == 0 ? Array.Empty<ChunkMeshData>() : _parts.ToArray();
        _parts.Clear();

        return result;
    }
}
