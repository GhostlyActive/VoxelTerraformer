using Raylib_cs;
using System.Numerics;

namespace Terraformer.Rendering;

/// <summary>
/// Wolkenschicht aus Voxeln: jede belegte Zelle des Himmelrasters wird zu einem Klumpen
/// aus Würfeln, deren Form aus einem Ellipsoid mit Hash-Rauschen kommt — dadurch runde,
/// unregelmäßige Wolken statt sichtbarer Kacheln. Die Schicht driftet nach Osten und
/// verschiebt sich zellenweise nahtlos.
/// </summary>
public sealed class CloudLayer
{
    private const float CellSize = 24f;
    private const float CubeSize = 4f;       // Voxelgröße der Wolken
    private const int BlobX = 6;             // Würfel je Achse im Klumpen
    private const int BlobY = 3;
    private const int BlobZ = 6;
    private const float Range = 250f;        // bis hinter das Fog-Ende, zieht mit dem Spieler mit

    public float Coverage { get; set; } = 0.35f;
    public float Height { get; set; } = 80f;
    public float DriftSpeed { get; set; } = 1.2f;   // Blöcke pro Sekunde

    public void Draw(Camera3D camera, float time, float daylight01, Vector3 center)
    {
        // tagsüber fast weiß, nachts dunkles Blaugrau
        Color color = Lerp(new Color(88, 98, 126, 255), new Color(250, 250, 255, 255), daylight01);

        float offset = time * DriftSpeed / CellSize;
        int shift = (int)MathF.Floor(offset);
        float slide = (offset - shift) * CellSize;

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);

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

            // Was klar hinter der Kamera liegt, kostet sonst nur Würfel ohne Bild
            Vector3 toCell = cellCenter - camera.Position;
            if (toCell.LengthSquared() > CellSize * CellSize && Vector3.Dot(toCell, forward) < 0f) continue;

            DrawBlob(cellCenter, cx - shift, cz, color, cube);
        }
    }

    /// <summary>Ein Klumpen: Würfel innerhalb eines Ellipsoids, dessen Rand per Hash ausgefranst wird</summary>
    private static void DrawBlob(Vector3 cellCenter, int hx, int hz, Color color, Vector3 cube)
    {
        // Jede Wolke bekommt eigene Proportionen, sonst wiederholt sich die Form zu deutlich
        float stretchX = 0.75f + Hash(hx, hz, 91) * 0.55f;
        float stretchZ = 0.75f + Hash(hx, hz, 92) * 0.55f;

        for (int iy = 0; iy < BlobY; iy++)
        for (int iz = 0; iz < BlobZ; iz++)
        for (int ix = 0; ix < BlobX; ix++)
        {
            // -1..1 relativ zur Klumpenmitte
            float nx = (ix - (BlobX - 1) / 2f) / (BlobX / 2f) / stretchX;
            float ny = (iy - (BlobY - 1) / 2f) / (BlobY / 2f);
            float nz = (iz - (BlobZ - 1) / 2f) / (BlobZ / 2f) / stretchZ;

            // Unterseite flacher als die Oberseite — so sitzen Wolken auf einer Basis auf
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
