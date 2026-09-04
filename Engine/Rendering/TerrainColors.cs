using Raylib_cs;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

public static class TerrainColors
{
    /// <summary>Blocks per axis that share a shade, as a power of two</summary>
    private const int VariationShift = 2;

    // Pure albedo: sun, ambient and AO are added during meshing and in the shader
    public static Color ForBlock(int id, int wx, int y, int wz)
    {
        BlockDef def = BlockRegistry.Get(id);
        Color baseColor = def.UseHeightGradient ? HeightColor(y) : def.BaseColor;

        // A slight variation so large surfaces do not look sterile. It runs per patch rather than
        // per voxel: two faces only merge in the greedy mesher when their colour matches exactly,
        // and a shade of its own for every block would leave nothing to merge.
        float variation = 0.94f + 0.12f * Hash(wx >> VariationShift, y >> VariationShift, wz >> VariationShift);
        return Scale(baseColor, variation);
    }

    /// <summary>
    /// The height the gradient below is laid out for; a taller world stretches the bands with
    /// it. Set by the terrain scene, read by the mesh workers, so change it before meshing starts.
    /// </summary>
    public static int WorldHeight { get; set; } = 64;

    // Height anchors for the terrain gradient, from the bottom of a basin to a snow cap, for a
    // world 64 blocks high. Colours in between are interpolated, so retuning the landscape is a
    // matter of moving these numbers.
    private static readonly (int Y, Vector3 Color)[] _bands =
    {
        (0,  new Vector3(10, 26, 78)),    // deep basin
        (4,  new Vector3(28, 66, 124)),   // shallow water
        (7,  new Vector3(198, 184, 132)), // shore
        (11, new Vector3(74, 152, 78)),   // grassland
        (23, new Vector3(52, 120, 58)),   // uplands
        (32, new Vector3(128, 104, 78)),  // bare rock
        (42, new Vector3(150, 146, 150)), // scree
        (52, new Vector3(240, 242, 248)), // snow
    };

    private static Color HeightColor(int y)
    {
        float scale = WorldHeight / 64f;
        if (y <= _bands[0].Y * scale) return ToColor(_bands[0].Color);

        for (int i = 1; i < _bands.Length; i++)
        {
            float top = _bands[i].Y * scale;
            if (y > top) continue;

            float t = InverseLerp(_bands[i - 1].Y * scale, top, y);
            return ToColor(Vector3.Lerp(_bands[i - 1].Color, _bands[i].Color, t));
        }

        return ToColor(_bands[^1].Color);
    }

    private static Color ToColor(Vector3 rgb)
        => new((byte)Math.Clamp(rgb.X, 0f, 255f), (byte)Math.Clamp(rgb.Y, 0f, 255f), (byte)Math.Clamp(rgb.Z, 0f, 255f), (byte)255);

    private static float Hash(int x, int y, int z)
    {
        unchecked
        {
            int h = x * 374761393;
            h ^= y * 668265263;
            h ^= z * 1274126177;
            h = (h ^ (h >> 13)) * 1103515245;
            h ^= h >> 16;
            return (h & 0x7fffffff) / 2147483647f;
        }
    }

    private static Color Scale(Color color, float factor) => new(
        (byte)Math.Clamp(color.R * factor, 0f, 255f),
        (byte)Math.Clamp(color.G * factor, 0f, 255f),
        (byte)Math.Clamp(color.B * factor, 0f, 255f),
        color.A);

    private static float InverseLerp(float a, float b, float v)
    {
        if (a == b) return 0f;
        return Math.Clamp((v - a) / (b - a), 0f, 1f);
    }
}
