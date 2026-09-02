using Raylib_cs;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

public static class TerrainColors
{
    // Pure albedo: sun, ambient and AO are added during meshing and in the shader
    public static Color ForBlock(int id, int wx, int y, int wz)
    {
        BlockDef def = BlockRegistry.Get(id);
        Color baseColor = def.UseHeightGradient ? HeightColor(y) : def.BaseColor;

        // A slight per-voxel variation, so large surfaces do not look sterile
        float variation = 0.94f + 0.12f * Hash(wx, y, wz);
        return Scale(baseColor, variation);
    }

    private static Color HeightColor(int y)
    {
        // Height bands (in block Y)
        const int deepBlueEndY = 8;      // 0..8 deep blue
        const int greenEndY = 15;        // ..15 green
        const int brownEndY = 24;        // ..24 brown, then the transition to snow

        // bottom: deep blue
        float dr = 10, dg = 25, db = 80;
        // green
        float gr = 60, gg = 190, gb = 70;
        // brown
        float br = 140, bg = 95, bb = 50;
        // white (snow)
        float wr = 235, wg = 235, wb = 235;

        float r, g, b;

        if (y <= deepBlueEndY)
        {
            float t = InverseLerp(0, deepBlueEndY, y);
            r = Lerp(dr, dr + 15, t);
            g = Lerp(dg, dg + 20, t);
            b = Lerp(db, db + 40, t);
        }
        else if (y <= greenEndY)
        {
            float t = InverseLerp(deepBlueEndY, greenEndY, y);
            r = Lerp(dr, gr, t);
            g = Lerp(dg, gg, t);
            b = Lerp(db, gb, t);
        }
        else if (y <= brownEndY)
        {
            float t = InverseLerp(greenEndY, brownEndY, y);
            r = Lerp(gr, br, t);
            g = Lerp(gg, bg, t);
            b = Lerp(gb, bb, t);
        }
        else
        {
            // soft transition brown -> white (+10 = width of the snow band)
            float t = InverseLerp(brownEndY, brownEndY + 10, y);
            r = Lerp(br, wr, t);
            g = Lerp(bg, wg, t);
            b = Lerp(bb, wb, t);
        }

        return new Color((byte)r, (byte)g, (byte)b, (byte)255);
    }

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

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float InverseLerp(float a, float b, float v)
    {
        if (a == b) return 0f;
        return Math.Clamp((v - a) / (b - a), 0f, 1f);
    }
}
