using Raylib_cs;
using System.Numerics;

namespace Terraformer.Rendering;

/// <summary>
/// Sternenhimmel: feste Richtungen auf der oberen Halbkugel, gezeichnet relativ zur Kamera,
/// damit sie wie unendlich weit weg wirken. Blendet mit der Nacht ein und funkelt leicht.
/// </summary>
public sealed class StarField
{
    private const int StarCount = 380;
    private const float Distance = 380f;

    private readonly Vector3[] _directions = new Vector3[StarCount];
    private readonly float[] _twinklePhase = new float[StarCount];
    private readonly float[] _sizes = new float[StarCount];

    public StarField()
    {
        var random = new Random(4213);

        for (int i = 0; i < StarCount; i++)
        {
            // Gleichverteilter Azimut; Elevation Richtung Horizont verdichtet,
            // weil man im Spiel meist flach über die Landschaft schaut
            float azimuth = (float)(random.NextDouble() * MathF.Tau);
            float elevation = (3f + MathF.Pow((float)random.NextDouble(), 1.6f) * 77f) * (MathF.PI / 180f);

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

        for (int i = 0; i < StarCount; i++)
        {
            float twinkle = 0.75f + 0.25f * MathF.Sin(time * 1.7f + _twinklePhase[i]);
            byte alpha = (byte)(255f * night01 * twinkle);

            Vector3 position = camera.Position + _directions[i] * Distance;
            Raylib.DrawCubeV(position, new Vector3(_sizes[i]), new Color((byte)235, (byte)240, (byte)255, alpha));
        }
    }
}
