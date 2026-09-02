using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// A starfield: fixed directions drawn relative to the camera, which makes them read as
/// infinitely far away. Fades in with the night and twinkles a little. Above terrain the upper
/// hemisphere is enough; in space it takes the full sphere and a distance beyond every celestial
/// body, both of which the constructor sets.
/// </summary>
public sealed class StarField
{
    private readonly int _starCount;
    private readonly float _distance;
    private readonly float _sizeScale;

    private readonly Vector3[] _directions;
    private readonly float[] _twinklePhase;
    private readonly float[] _sizes;

    public StarField(bool fullSphere = false, float distance = 380f, int starCount = 380)
    {
        _starCount = starCount;
        _distance = distance;

        // The cubes sit at a fixed distance, so when that grows they have to grow with it, or the
        // stars shrink to subpixel size and flicker
        _sizeScale = distance / 380f;

        _directions = new Vector3[starCount];
        _twinklePhase = new float[starCount];
        _sizes = new float[starCount];

        var random = new Random(4213);

        for (int i = 0; i < starCount; i++)
        {
            // Evenly distributed azimuth; above terrain the elevation is packed towards the
            // horizon, because that is where you mostly look
            float azimuth = (float)(random.NextDouble() * MathF.Tau);
            float elevation = fullSphere
                ? MathF.Asin((float)(random.NextDouble() * 2.0 - 1.0))
                : (3f + MathF.Pow((float)random.NextDouble(), 1.6f) * 77f) * (MathF.PI / 180f);

            _directions[i] = new Vector3(
                MathF.Cos(elevation) * MathF.Cos(azimuth),
                MathF.Sin(elevation),
                MathF.Cos(elevation) * MathF.Sin(azimuth));

            _twinklePhase[i] = (float)(random.NextDouble() * MathF.Tau);
            _sizes[i] = 1.1f + (float)random.NextDouble() * 1.5f;
        }
    }

    public void Draw(Camera3D camera, float night01, float time)
    {
        if (night01 <= 0.03f) return;

        for (int i = 0; i < _starCount; i++)
        {
            float twinkle = 0.75f + 0.25f * MathF.Sin(time * 1.7f + _twinklePhase[i]);
            byte alpha = (byte)(255f * night01 * twinkle);

            Vector3 position = camera.Position + _directions[i] * _distance;
            Raylib.DrawCubeV(position, new Vector3(_sizes[i] * _sizeScale), new Color((byte)235, (byte)240, (byte)255, alpha));
        }
    }
}
