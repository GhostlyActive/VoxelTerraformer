using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// Sternenhimmel: feste Richtungen, gezeichnet relativ zur Kamera, damit sie wie unendlich
/// weit weg wirken. Blendet mit der Nacht ein und funkelt leicht. Über dem Gelände genügt die
/// obere Halbkugel; im Weltraum braucht es die volle Kugel und einen Abstand jenseits aller
/// Himmelskörper — beides stellt der Konstruktor ein.
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

        // Die Würfel stehen in fester Entfernung — wächst die, müssen sie mitwachsen,
        // sonst schrumpfen die Sterne auf Subpixelgröße und flackern
        _sizeScale = distance / 380f;

        _directions = new Vector3[starCount];
        _twinklePhase = new float[starCount];
        _sizes = new float[starCount];

        var random = new Random(4213);

        for (int i = 0; i < starCount; i++)
        {
            // Gleichverteilter Azimut; über Gelände ist die Elevation Richtung Horizont
            // verdichtet, weil man dort meist flach über die Landschaft schaut
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
