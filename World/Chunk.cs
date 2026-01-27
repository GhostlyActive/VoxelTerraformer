using Raylib_cs;
using System.Numerics;
using System.Collections.Generic;

namespace Terraformer.World;

public class Chunk
{
    public const int Size = 16;

    // Using a flat array is faster than a 3D array.
    // Layout: x + Size * (z + Size * y)
    private readonly byte[] _blocks;

    public readonly ChunkCoord Coord;
    public readonly Vector3 WorldPosition;

    // Dirty flag: rebuild visible cache only when needed.
    private bool _dirty = true;

    // Cached positions (cube centers) for blocks that are visible.
    private readonly List<Vector3> _visibleBlocks = new();

    public Chunk(ChunkCoord coord, int worldHeight)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);

        _blocks = new byte[Size * worldHeight * Size];

        GenerateTerrain(worldHeight);
    }

    public void MarkDirty() => _dirty = true;

    public int GetLocal(int x, int y, int z, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return 0;
        return _blocks[Index(x, y, z)];
    }

    public void SetLocal(int x, int y, int z, int id, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return;

        _blocks[Index(x, y, z)] = (byte)Math.Clamp(id, 0, 255);
        _dirty = true;
    }

    public void Draw(Vector3 sunPos, int worldHeight, Func<int, int, int, int> getWorldBlock)
    {
        if (_dirty)
            RebuildVisibleList(worldHeight, getWorldBlock);

        // Simple daylight factor.
        float brightness = Math.Clamp(sunPos.Y / 30.0f, 0.35f, 1.0f);

        Color blockColor = new Color
        {
            R = (byte)(60 * brightness),
            G = (byte)(190 * brightness),
            B = (byte)(70 * brightness),
            A = 255
        };

        foreach (var center in _visibleBlocks)
        {
            Raylib.DrawCube(center, 1f, 1f, 1f, blockColor);
            Raylib.DrawCubeWires(center, 1f, 1f, 1f, Color.DarkGreen);
        }
    }

    // --- Internals ---

    private void GenerateTerrain(int worldHeight)
    {
        // Simple heightmap. Replace later with noise for better terrain.
        for (int x = 0; x < Size; x++)
        for (int z = 0; z < Size; z++)
        {
            int wx = (int)WorldPosition.X + x;
            int wz = (int)WorldPosition.Z + z;

            int h = (int)(MathF.Sin(wx * 0.25f) * 2f + MathF.Cos(wz * 0.25f) * 2f + 6f);
            h = Math.Clamp(h, 1, worldHeight - 1);

            for (int y = 0; y <= h; y++)
                _blocks[Index(x, y, z)] = 1;
        }

        _dirty = true;
    }

    private void RebuildVisibleList(int worldHeight, Func<int, int, int, int> getWorldBlock)
    {
        _visibleBlocks.Clear();

        // Add only blocks with at least one air neighbor (basic face culling).
        for (int x = 0; x < Size; x++)
        for (int y = 0; y < worldHeight; y++)
        for (int z = 0; z < Size; z++)
        {
            if (_blocks[Index(x, y, z)] == 0) continue;

            int wx = (int)WorldPosition.X + x;
            int wz = (int)WorldPosition.Z + z;

            bool visible =
                getWorldBlock(wx + 1, y, wz) == 0 ||
                getWorldBlock(wx - 1, y, wz) == 0 ||
                getWorldBlock(wx, y + 1, wz) == 0 ||
                getWorldBlock(wx, y - 1, wz) == 0 ||
                getWorldBlock(wx, y, wz + 1) == 0 ||
                getWorldBlock(wx, y, wz - 1) == 0;

            if (!visible) continue;

            _visibleBlocks.Add(new Vector3(wx + 0.5f, y + 0.5f, wz + 0.5f));
        }

        _dirty = false;
    }

    private static bool InBounds(int x, int y, int z, int worldHeight)
        => x >= 0 && x < Size && z >= 0 && z < Size && y >= 0 && y < worldHeight;

    private static int Index(int x, int y, int z)
        => x + Size * (z + Size * y);
}
