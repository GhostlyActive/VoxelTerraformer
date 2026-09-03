using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// A cloud layer made of voxels: every occupied cell of the sky grid becomes a lump of cubes
/// whose shape comes from an ellipsoid roughened with hash noise, which gives rounded, irregular
/// clouds instead of visible tiles. The layer drifts east and wraps cell by cell without a seam.
/// </summary>
public sealed class CloudLayer
{
    // Cell and cube size scale with the range, so a wider sky costs the same number of blocks and
    // the clouds keep roughly the same size on screen
    private const float CellSize = 46f;
    private const float CubeSize = 7f;       // voxel size of the clouds
    private const int BlobX = 6;             // cubes per axis in a lump
    private const int BlobY = 3;
    private const int BlobZ = 6;
    private const float Range = 780f;        // out past the fog, and it travels with the player

    public float Coverage { get; set; } = 0.35f;
    public float Height { get; set; } = 80f;
    public float DriftSpeed { get; set; } = 1.2f;   // blocks per second

    /// <summary>
    /// <paramref name="frustum"/> is the same one the terrain is culled against. Without it the
    /// layer builds every cell of a sky nearly two kilometres across, of which the player sees
    /// maybe a quarter.
    /// </summary>
    public void Draw(Camera3D camera, Frustum frustum, float time, float daylight01, Vector3 center)
    {
        // nearly white by day, dark blue-grey at night
        Color color = Lerp(new Color(88, 98, 126, 255), new Color(250, 250, 255, 255), daylight01);

        float offset = time * DriftSpeed / CellSize;
        int shift = (int)MathF.Floor(offset);
        float slide = (offset - shift) * CellSize;

        // Half the extent of a lump, for the box the frustum is tested against
        var blobExtent = new Vector3(BlobX * CubeSize * 0.7f, BlobY * CubeSize * 0.7f, BlobZ * CubeSize * 0.7f);

        int minX = (int)MathF.Floor((center.X - Range) / CellSize) - 1;
        int maxX = (int)MathF.Floor((center.X + Range) / CellSize);
        int minZ = (int)MathF.Floor((center.Z - Range) / CellSize);
        int maxZ = (int)MathF.Floor((center.Z + Range) / CellSize);

        var cube = new Vector3(CubeSize);

        for (int cz = minZ; cz <= maxZ; cz++)
        for (int cx = minX; cx <= maxX; cx++)
        {
            if (Hash(cx - shift, cz, 0) > Coverage) continue;

            var cellCenter = new Vector3(
                cx * CellSize + CellSize / 2f + slide,
                Height,
                cz * CellSize + CellSize / 2f);

            if (!frustum.Intersects(cellCenter - blobExtent, cellCenter + blobExtent)) continue;

            DrawBlob(cellCenter, cx - shift, cz, color, cube);
        }
    }

    /// <summary>One lump: cubes inside an ellipsoid whose edge is frayed with a hash</summary>
    private static void DrawBlob(Vector3 cellCenter, int hx, int hz, Color color, Vector3 cube)
    {
        // Every cloud gets its own proportions, or the shape repeats too obviously
        float stretchX = 0.75f + Hash(hx, hz, 91) * 0.55f;
        float stretchZ = 0.75f + Hash(hx, hz, 92) * 0.55f;

        for (int iy = 0; iy < BlobY; iy++)
        for (int iz = 0; iz < BlobZ; iz++)
        for (int ix = 0; ix < BlobX; ix++)
        {
            // -1..1 relative to the centre of the lump
            float nx = (ix - (BlobX - 1) / 2f) / (BlobX / 2f) / stretchX;
            float ny = (iy - (BlobY - 1) / 2f) / (BlobY / 2f);
            float nz = (iz - (BlobZ - 1) / 2f) / (BlobZ / 2f) / stretchZ;

            // The underside is flatter than the top, so clouds sit on a base
            float squash = ny < 0f ? 1.5f : 1f;
            float distance = nx * nx + (ny * squash) * (ny * squash) + nz * nz;

            float threshold = 0.75f + Hash(hx, hz, ix + BlobX * (iy + BlobY * iz) + 7) * 0.45f;
            if (distance > threshold) continue;

            var position = new Vector3(
                cellCenter.X + (ix - (BlobX - 1) / 2f) * CubeSize,
                cellCenter.Y + (iy - (BlobY - 1) / 2f) * CubeSize,
                cellCenter.Z + (iz - (BlobZ - 1) / 2f) * CubeSize);

            Raylib.DrawCubeV(position, cube, color);
        }
    }

    private static float Hash(int x, int z, int salt)
    {
        unchecked
        {
            int h = x * 374761393 + z * 668265263 + salt * unchecked((int)2246822519);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x7fffffff) / 2147483647f;
        }
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }
}
