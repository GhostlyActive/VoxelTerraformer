using System;

namespace Terraformer.World
{
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
                // 0..1
                return (h & 0x7fffffff) / 2147483647f;
            }
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // Value noise (2D) mit bilinear interpolation -> 0..1
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

            float ab = Lerp(a, b, tx);
            float cd = Lerp(c, d, tx);
            return Lerp(ab, cd, tz);
        }

        // fBm: mehrere Oktaven -> 0..1-ish (wir clampen am Ende)
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

        public static float RidgeFbm2D(float x, float z, int seed, int octaves, float persistence, float lacunarity)
        {
            float sum = 0f;
            float amp = 1f;
            float freq = 1f;
            float norm = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = Value2D(x * freq, z * freq, seed + i * 1013); // 0..1
                                                                        // ridged: Peaks -> 1, Täler -> 0
                n = 1f - MathF.Abs(n * 2f - 1f); // 0..1, “ridge”
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
}
