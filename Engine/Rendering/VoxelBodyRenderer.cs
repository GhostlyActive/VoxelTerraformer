using Raylib_cs;
using System.Buffers;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Draws <see cref="VoxelBody"/> objects and holds their GPU meshes. A body is remeshed as soon as
/// it is marked dirty, but only a few per frame: a hit that touches several bodies at once would
/// otherwise tear into the frame rate.
/// </summary>
public sealed class VoxelBodyRenderer : IDisposable
{
    private const int MaxRebuildsPerFrame = 2;

    private sealed class Entry
    {
        public Mesh Mesh;
        public bool HasMesh;
    }

    private static readonly Dictionary<int, byte[]> _noRefinements = new();

    private readonly Dictionary<VoxelBody, Entry> _entries = new();
    private int _rebuildsThisFrame;

    /// <summary>Call at the start of every frame; resets the meshing budget</summary>
    public void BeginFrame() => _rebuildsThisFrame = 0;

    /// <summary>
    /// Draw a body. The light is set per body, because out in space every sphere is hit by the sun
    /// from a different direction, and because the body's own spin would otherwise drag the lit
    /// side around with it.
    /// </summary>
    public void Draw(VoxelBody body, TerrainShader shader, Vector3 sunPosition, Vector3 sunColor, Vector3 ambient, Vector3 cameraPosition)
    {
        Entry entry = EntryFor(body);
        if (!entry.HasMesh) return;

        Vector3 toBody = body.Position - sunPosition;
        Vector3 sunDirection = toBody.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(toBody);

        shader.SetLighting(
            body.DirectionToLocal(sunDirection), sunColor, ambient,
            new Color(0, 0, 0, 255), cameraPosition);

        Raylib.DrawMesh(entry.Mesh, shader.Material, TransformOf(body));
    }

    /// <summary>Model matrix: grid centre to the origin, scale, rotate, then out to the world position</summary>
    private static Matrix4x4 TransformOf(VoxelBody body)
    {
        const float half = VoxelBody.Size * 0.5f;

        return Raymath.MatrixMultiply(
            Raymath.MatrixMultiply(
                Raymath.MatrixTranslate(-half, -half, -half),
                Raymath.MatrixScale(body.VoxelScale, body.VoxelScale, body.VoxelScale)),
            Raymath.MatrixMultiply(
                Raymath.MatrixRotateY(body.Spin),
                Raymath.MatrixTranslate(body.Position.X, body.Position.Y, body.Position.Z)));
    }

    private Entry EntryFor(VoxelBody body)
    {
        if (!_entries.TryGetValue(body, out Entry? entry))
        {
            entry = new Entry();
            _entries[body] = entry;
        }

        if (!body.Dirty) return entry;
        if (entry.HasMesh && _rebuildsThisFrame >= MaxRebuildsPerFrame) return entry;

        _rebuildsThisFrame++;
        Rebuild(body, entry);
        body.MarkClean();

        return entry;
    }

    private static void Rebuild(VoxelBody body, Entry entry)
    {
        byte[] padded = ArrayPool<byte>.Shared.Rent(ChunkMesher.PaddedLength(VoxelBody.Size));
        Array.Clear(padded, 0, ChunkMesher.PaddedLength(VoxelBody.Size)); // the shell is air: the body ends at its grid

        for (int y = 0; y < VoxelBody.Size; y++)
        for (int z = 0; z < VoxelBody.Size; z++)
        for (int x = 0; x < VoxelBody.Size; x++)
            padded[ChunkMesher.Index(x, y, z)] = (byte)body.Get(x, y, z);

        ChunkMeshData data = ChunkMesher.Build(padded, _noRefinements, VoxelBody.Size, 0, 0);
        ArrayPool<byte>.Shared.Return(padded);

        if (entry.HasMesh)
        {
            Raylib.UnloadMesh(entry.Mesh);
            entry.HasMesh = false;
        }

        if (data.VertexCount == 0) return;

        var mesh = new Mesh(data.VertexCount, data.VertexCount / 3);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocColors();
        data.Vertices.AsSpan(0, data.VertexCount * 3).CopyTo(mesh.VerticesAs<float>());
        data.Normals.AsSpan(0, data.VertexCount * 3).CopyTo(mesh.NormalsAs<float>());
        data.Colors.AsSpan(0, data.VertexCount * 4).CopyTo(mesh.ColorsAs<byte>());
        Raylib.UploadMesh(ref mesh, false);

        entry.Mesh = mesh;
        entry.HasMesh = true;
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values)
            if (entry.HasMesh)
                Raylib.UnloadMesh(entry.Mesh);

        _entries.Clear();
    }
}
