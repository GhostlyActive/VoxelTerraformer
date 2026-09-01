namespace VoxelEngine.MathTools;

/// <summary>
/// Deterministisches Value-Noise für Geländehöhen (2D) und Oberflächen von Voxel-Körpern (3D).
/// Derselbe Seed liefert überall dasselbe Ergebnis — Chunks passen an ihren Grenzen zusammen.
/// </summary>
public static class Noise
{
    private static float Frac(float v) => v - MathF.Floor(v);

    // Deterministischer Hash -> 0..1
    private static float Hash2(int x, int z, int seed)
    {
        unchecked
        {
            int h = seed;
            h ^= x * 374761393;
            h = (h << 13) ^ h;
            h ^= z * 668265263;
            h = (h << 17) ^ h;
            h *= 1274126177;
            return (h & 0x7fffffff) / 2147483647f;
        }
    }

    private static float Hash3(int x, int y, int z, int seed)
    {
        unchecked
        {
            int h = seed;
            h ^= x * 374761393;
            h = (h << 13) ^ h;
            h ^= y * 668265263;
            h = (h << 11) ^ h;
            h ^= z * 1274126177;
            h = (h << 17) ^ h;
            h *= 951274213;
            return (h & 0x7fffffff) / 2147483647f;
        }
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Value Noise, bilinear interpoliert -> 0..1</summary>
    public static float Value2D(float x, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int z0 = (int)MathF.Floor(z);
        int x1 = x0 + 1;
        int z1 = z0 + 1;

        float tx = Smooth(Frac(x));
        float tz = Smooth(Frac(z));

        float a = Hash2(x0, z0, seed);
        float b = Hash2(x1, z0, seed);
        float c = Hash2(x0, z1, seed);
        float d = Hash2(x1, z1, seed);

        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    /// <summary>Value Noise, trilinear interpoliert -> 0..1</summary>
    public static float Value3D(float x, float y, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);

        float tx = Smooth(Frac(x));
        float ty = Smooth(Frac(y));
        float tz = Smooth(Frac(z));

        float lowerFront = Lerp(Hash3(x0, y0, z0, seed), Hash3(x0 + 1, y0, z0, seed), tx);
        float lowerBack = Lerp(Hash3(x0, y0, z0 + 1, seed), Hash3(x0 + 1, y0, z0 + 1, seed), tx);
        float upperFront = Lerp(Hash3(x0, y0 + 1, z0, seed), Hash3(x0 + 1, y0 + 1, z0, seed), tx);
        float upperBack = Lerp(Hash3(x0, y0 + 1, z0 + 1, seed), Hash3(x0 + 1, y0 + 1, z0 + 1, seed), tx);

        return Lerp(Lerp(lowerFront, lowerBack, tz), Lerp(upperFront, upperBack, tz), ty);
    }

    /// <summary>Mehrere Oktaven übereinander -> 0..1</summary>
    public static float Fbm2D(float x, float z, int seed, int octaves, float persistence, float lacunarity)
    {
        float sum = 0f;
        float amp = 1f;
        float freq = 1f;
        float norm = 0f;

        for (int i = 0; i < octaves; i++)
        {
            sum += Value2D(x * freq, z * freq, seed + i * 1013) * amp;
            norm += amp;

            amp *= persistence;
            freq *= lacunarity;
        }

        if (norm <= 1e-6f) return 0f;
        return Math.Clamp(sum / norm, 0f, 1f);
    }

    public static float Fbm3D(float x, float y, float z, int seed, int octaves, float persistence, float lacunarity)
    {
        float sum = 0f;
        float amp = 1f;
        float freq = 1f;
        float norm = 0f;

        for (int i = 0; i < octaves; i++)
        {
            sum += Value3D(x * freq, y * freq, z * freq, seed + i * 1013) * amp;
            norm += amp;

            amp *= persistence;
            freq *= lacunarity;
        }

        if (norm <= 1e-6f) return 0f;
        return Math.Clamp(sum / norm, 0f, 1f);
    }

    /// <summary>Gefaltetes fBm: Peaks bei 1, Täler bei 0 — ergibt Bergketten statt Hügeln</summary>
    public static float RidgeFbm2D(float x, float z, int seed, int octaves, float persistence, float lacunarity)
    {
        float sum = 0f;
        float amp = 1f;
        float freq = 1f;
        float norm = 0f;

        for (int i = 0; i < octaves; i++)
        {
            float n = Value2D(x * freq, z * freq, seed + i * 1013);
            n = 1f - MathF.Abs(n * 2f - 1f);
            sum += n * amp;
            norm += amp;
            amp *= persistence;
            freq *= lacunarity;
        }

        return norm > 1e-6f ? Math.Clamp(sum / norm, 0f, 1f) : 0f;
    }

    public static float SmoothStep(float a, float b, float t)
    {
        t = Math.Clamp((t - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
