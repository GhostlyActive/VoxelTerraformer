using Raylib_cs;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// Draws <see cref="VoxelBody"/> objects and holds their GPU meshes, one per sub-chunk. A hit only
/// dirties the sub-chunks it touched, so a shot into a planet of a hundred voxels across rebuilds
/// a few thousand voxels instead of a million.
/// </summary>
public sealed class VoxelBodyRenderer : IDisposable
{
    /// <summary>Sub-chunks remeshed per frame while the game is running</summary>
    private const int MaxRebuildsPerFrame = 6;

    private sealed class Entry
    {
        public required Mesh[] Meshes { get; init; }
        public required bool[] HasMesh { get; init; }
    }

    private readonly Dictionary<VoxelBody, Entry> _entries = new();
    private int _rebuildsThisFrame;

    /// <summary>Call at the start of every frame; resets the meshing budget</summary>
    public void BeginFrame() => _rebuildsThisFrame = 0;

    /// <summary>
    /// Mesh a whole body up front, ignoring the per-frame budget. Meant for loading: without it a
    /// fresh system would visibly pop into existence over the first second.
    /// </summary>
    public void Prewarm(VoxelBody body)
    {
        Entry entry = EntryFor(body);

        for (int i = 0; i < body.ChunkCount; i++)
        {
            if (!body.IsChunkDirty(i)) continue;

            Rebuild(body, entry, i);
            body.MarkChunkClean(i);
        }
    }

    /// <summary>
    /// Draw a body. The light is set per body, because out in space every sphere is hit by the sun
    /// from a different direction. <paramref name="shadowCasters"/> are spheres that can block that
    /// sunlight (xyz centre, w radius) — that is how a moon puts a shadow on its planet.
    /// </summary>
    public void Draw(
        VoxelBody body,
        TerrainShader shader,
        Vector3 sunPosition,
        Vector3 sunColor,
        Vector3 ambient,
        Vector3 cameraPosition,
        ReadOnlySpan<Vector4> shadowCasters)
    {
        Entry entry = EntryFor(body);

        Vector3 toBody = body.Position - sunPosition;
        Vector3 sunDirection = toBody.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(toBody);

        shader.SetLighting(sunDirection, sunColor, ambient, new Color(0, 0, 0, 255), cameraPosition);
        shader.SetShadowCasters(shadowCasters);

        Matrix4x4 transform = TransformOf(body);

        for (int i = 0; i < body.ChunkCount; i++)
        {
            if (body.IsChunkDirty(i) && _rebuildsThisFrame < MaxRebuildsPerFrame)
            {
                _rebuildsThisFrame++;
                Rebuild(body, entry, i);
                body.MarkChunkClean(i);
            }

            if (entry.HasMesh[i]) Raylib.DrawMesh(entry.Meshes[i], shader.Material, transform);
        }
    }

    /// <summary>Model matrix: grid centre to the origin, scale, rotate, then out to the world position</summary>
    private static Matrix4x4 TransformOf(VoxelBody body)
    {
        float half = body.Size * 0.5f;

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
        if (_entries.TryGetValue(body, out Entry? entry)) return entry;

        entry = new Entry
        {
            Meshes = new Mesh[body.ChunkCount],
            HasMesh = new bool[body.ChunkCount],
        };
        _entries[body] = entry;

        return entry;
    }

    private static void Rebuild(VoxelBody body, Entry entry, int chunkIndex)
    {
        (int originX, int originY, int originZ) = body.ChunkOrigin(chunkIndex);
        ChunkMeshData data = VoxelBodyMesher.Build(body, originX, originY, originZ);

        if (entry.HasMesh[chunkIndex])
        {
            Raylib.UnloadMesh(entry.Meshes[chunkIndex]);
            entry.HasMesh[chunkIndex] = false;
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

        entry.Meshes[chunkIndex] = mesh;
        entry.HasMesh[chunkIndex] = true;
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values)
            for (int i = 0; i < entry.Meshes.Length; i++)
                if (entry.HasMesh[i])
                    Raylib.UnloadMesh(entry.Meshes[i]);

        _entries.Clear();
    }
}
