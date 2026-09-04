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
    private const int MaxRebuildsPerFrame = 24;

    private sealed class Entry
    {
        public required GpuMesh[]?[] Parts { get; init; }
    }

    private readonly Dictionary<VoxelBody, Entry> _entries = new();
    private readonly MeshBuilder _builder = new();
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
            if (!body.IsChunkDirty(i) || _rebuildsThisFrame >= MaxRebuildsPerFrame) continue;

            _rebuildsThisFrame++;
            Rebuild(body, entry, i);
            body.MarkChunkClean(i);
        }

        MeshDrawer.Begin(shader.Shader);

        for (int i = 0; i < body.ChunkCount; i++)
        {
            GpuMesh[]? parts = entry.Parts[i];
            if (parts == null) continue;

            foreach (GpuMesh part in parts)
                MeshDrawer.Draw(part, transform);
        }

        MeshDrawer.End();
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

        entry = new Entry { Parts = new GpuMesh[]?[body.ChunkCount] };
        _entries[body] = entry;

        return entry;
    }

    private void Rebuild(VoxelBody body, Entry entry, int chunkIndex)
    {
        (int originX, int originY, int originZ) = body.ChunkOrigin(chunkIndex);
        ChunkMeshData[] data = VoxelBodyMesher.Build(_builder, body, originX, originY, originZ);

        Free(entry, chunkIndex);

        if (data.Length == 0) return;

        var parts = new GpuMesh[data.Length];
        for (int i = 0; i < data.Length; i++)
            parts[i] = new GpuMesh(data[i].Vertices.AsSpan(0, data[i].VertexCount), data[i].Indices.AsSpan(0, data[i].IndexCount));

        entry.Parts[chunkIndex] = parts;
    }

    private static void Free(Entry entry, int chunkIndex)
    {
        GpuMesh[]? parts = entry.Parts[chunkIndex];
        if (parts == null) return;

        foreach (GpuMesh part in parts) part.Dispose();
        entry.Parts[chunkIndex] = null;
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values)
            for (int i = 0; i < entry.Parts.Length; i++)
                Free(entry, i);

        _entries.Clear();
    }
}
