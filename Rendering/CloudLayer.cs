using Raylib_cs;
using System.Numerics;
using Terraformer.World;

namespace Terraformer.Rendering;

/// <summary>
/// Blockige, halbtransparente Wolkenschicht über der Welt, driftet langsam nach Osten.
/// Das Muster kommt aus einem Hash pro Zelle; die Schicht verschiebt sich zellenweise nahtlos.
/// </summary>
public sealed class CloudLayer
{
    private const float CellSize = 8f;
    private const float CloudY = 80f;
    private const float Thickness = 3f;
    private const float DriftSpeed = 1.2f;   // Blöcke pro Sekunde
    private const float Coverage = 0.32f;
    private const float Range = 260f;        // bis hinter das Fog-Ende, zieht mit dem Spieler mit

    public void Draw(float time, float daylight01, Vector3 center)
    {
        // tagsüber fast weiß, nachts dunkles Blaugrau
        Color color = Lerp(
            new Color(85, 95, 120, 110),
            new Color(250, 250, 255, 150),
            daylight01);

        float offset = time * DriftSpeed / CellSize;
        int shift = (int)MathF.Floor(offset);
        float slide = (offset - shift) * CellSize;

        int minX = (int)MathF.Floor((center.X - Range) / CellSize) - 1;
        int maxX = (int)MathF.Floor((center.X + Range) / CellSize);
        int minZ = (int)MathF.Floor((center.Z - Range) / CellSize);
        int maxZ = (int)MathF.Floor((center.Z + Range) / CellSize);

        for (int cz = minZ; cz <= maxZ; cz++)
        for (int cx = minX; cx <= maxX; cx++)
        {
            if (Hash(cx - shift, cz) > Coverage) continue;

            var cloudCenter = new Vector3(
                cx * CellSize + CellSize / 2f + slide,
                CloudY,
                cz * CellSize + CellSize / 2f);

            Raylib.DrawCubeV(cloudCenter, new Vector3(CellSize - 0.6f, Thickness, CellSize - 0.6f), color);
        }
    }

    private static float Hash(int x, int z)
    {
        unchecked
        {
            int h = x * 374761393 + z * 668265263;
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
