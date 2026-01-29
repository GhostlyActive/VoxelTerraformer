using Raylib_cs;
using System.Numerics;
using System.Collections.Generic;

namespace Terraformer.World;

public class Chunk
{
    public const int Size = 32;

    // Layout: x + Size * (z + Size * y)
    private readonly byte[] _blocks;

    public readonly ChunkCoord Coord;
    public readonly Vector3 WorldPosition;

    private bool _dirty = true;

    // Wir speichern Center + Y-Höhe
    private readonly List<(Vector3 center, int y)> _visibleBlocks = new();

    public Chunk(ChunkCoord coord, int worldHeight)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);
        _blocks = new byte[Size * worldHeight * Size];
        GenerateTerrain(worldHeight);
    }

    public void MarkDirty() => _dirty = true;

    public int GetLocal(int x, int y, int z, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return 0;
        return _blocks[Index(x, y, z)];
    }

    public void SetLocal(int x, int y, int z, int id, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return;
        _blocks[Index(x, y, z)] = (byte)Math.Clamp(id, 0, 255);
        _dirty = true;
    }

    public void Draw(
        Vector3 sunPos,
        int worldHeight,
        Func<int, int, int, int> getWorldBlock)
    {
        if (_dirty)
            RebuildVisibleList(worldHeight, getWorldBlock);

        float brightness = Math.Clamp(sunPos.Y / 30.0f, 0.35f, 1.0f);

        foreach (var (center, y) in _visibleBlocks)
        {
            Color blockColor = HeightColor(y, brightness);


            Raylib.DrawCube(center, 1f, 1f, 1f, blockColor);
            Raylib.DrawCubeWires(center, 1f, 1f, 1f, Color.DarkGreen);
        }
    }

    // -------------------------------------------------
    // Terrain
    // -------------------------------------------------

    private void GenerateTerrain(int worldHeight)
    {
        const int seed = 1337;

        // --- Große Form: Kontinente / Lowlands vs Highlands ---
        const float continentScale = 0.012f;   // sehr groß
        const int continentOct = 4;
        const float continentAmp = 24f;        // macht große Höhenunterschiede

        // --- Berge: Ridged Noise (Bergketten) ---
        const float mountainScale = 0.035f;    // mittlere Größe
        const int mountainOct = 5;
        const float mountainAmp = 26f;

        // --- Feindetail ---
        const float detailScale = 0.09f;
        const int detailOct = 3;
        const float detailAmp = 5f;

        const int baseHeight = 1;             // “Meeresspiegel / Grund”

        for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
            {
                int wx = (int)WorldPosition.X + x;
                int wz = (int)WorldPosition.Z + z;

                // 0..1
                float continents = Noise.Fbm2D(wx * continentScale, wz * continentScale, seed, continentOct, 0.5f, 2.0f);
                float mountains = Noise.RidgeFbm2D(wx * mountainScale, wz * mountainScale, seed + 9000, mountainOct, 0.5f, 2.0f);
                float detail = Noise.Fbm2D(wx * detailScale, wz * detailScale, seed + 42000, detailOct, 0.55f, 2.0f);

                // Kontinente “shapen”: mehr echte Lowlands + echte Highlands
                // (Pow < 1 hebt Lowlands an, Pow > 1 macht mehr Lowlands; hier: mehr Täler)
                float contShaped = MathF.Pow(continents, 2.1f);

                // Mountain-Maske: Berge eher im “Hochland”, weniger in tiefen Ebenen
                float mountainMask = Noise.SmoothStep(0.45f, 0.75f, contShaped);

                // Höhe zusammensetzen
                float heightF =
                    baseHeight +
                    contShaped * continentAmp +
                    mountains * mountainAmp * mountainMask +
                    (detail - 0.5f) * 2f * detailAmp; // detail um 0 zentriert

                int h = (int)MathF.Floor(heightF);
                h = Math.Clamp(h, 1, worldHeight - 2);

                for (int y = 0; y <= h; y++)
                    _blocks[Index(x, y, z)] = 1;

                // Optional: wenn du “Meer” willst, kannst du später Wasserblöcke < seaLevel füllen.
                // (aktuell hast du nur 1 Blocktyp)
            }

        _dirty = true;
    }



    private void RebuildVisibleList(
        int worldHeight,
        Func<int, int, int, int> getWorldBlock)
    {
        _visibleBlocks.Clear();

        for (int x = 0; x < Size; x++)
            for (int y = 0; y < worldHeight; y++)
                for (int z = 0; z < Size; z++)
                {
                    if (_blocks[Index(x, y, z)] == 0)
                        continue;

                    int wx = (int)WorldPosition.X + x;
                    int wz = (int)WorldPosition.Z + z;

                    bool visible =
                        getWorldBlock(wx + 1, y, wz) == 0 ||
                        getWorldBlock(wx - 1, y, wz) == 0 ||
                        getWorldBlock(wx, y + 1, wz) == 0 ||
                        getWorldBlock(wx, y - 1, wz) == 0 ||
                        getWorldBlock(wx, y, wz + 1) == 0 ||
                        getWorldBlock(wx, y, wz - 1) == 0;

                    if (!visible) continue;

                    _visibleBlocks.Add(
                        (new Vector3(wx + 0.5f, y + 0.5f, wz + 0.5f), y));
                }

        _dirty = false;
    }

    // -------------------------------------------------
    // Farbe nach Höhe
    // -------------------------------------------------

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float InverseLerp(float a, float b, float v)
    {
        if (a == b) return 0f;
        return Math.Clamp((v - a) / (b - a), 0f, 1f);
    }

    private static Color HeightColor(int y, float brightness)
    {
        // ✅ Höhen-Grenzen (in Block-Y) — HIER stellst du es ein:
        const int deepBlueEndY = 8;      // 0..6 dunkelblau
        const int greenEndY = 15;     // 7..18 grün
        const int brownEndY = 24;     // 19..28 braun
                                      // ab 29 -> weiß

        // Farben (Basis)
        // unten: dunkelblau
        float dr = 10, dg = 25, db = 80;

        // grün
        float gr = 60, gg = 190, gb = 70;

        // braun
        float br = 140, bg = 95, bb = 50;

        // weiß (Schnee)
        float wr = 235, wg = 235, wb = 235;

        float r, g, b;

        if (y <= deepBlueEndY)
        {
            // Optional: leicht von sehr dunkel zu weniger dunkel innerhalb der Zone
            float t = InverseLerp(0, deepBlueEndY, y);
            r = Lerp(dr, dr + 15, t);
            g = Lerp(dg, dg + 20, t);
            b = Lerp(db, db + 40, t);
        }
        else if (y <= greenEndY)
        {
            // Übergang dunkelblau -> grün (weich)
            float t = InverseLerp(deepBlueEndY, greenEndY, y);
            r = Lerp(dr, gr, t);
            g = Lerp(dg, gg, t);
            b = Lerp(db, gb, t);
        }
        else if (y <= brownEndY)
        {
            // Übergang grün -> braun (weich)
            float t = InverseLerp(greenEndY, brownEndY, y);
            r = Lerp(gr, br, t);
            g = Lerp(gg, bg, t);
            b = Lerp(gb, bb, t);
        }
        else
        {
            // Übergang braun -> weiß (weich, damit Schnee nicht wie Cut aussieht)
            float t = InverseLerp(brownEndY, brownEndY + 10, y); // +10 = Schneebandbreite
            r = Lerp(br, wr, t);
            g = Lerp(bg, wg, t);
            b = Lerp(bb, wb, t);
        }

        // Licht anwenden
        r = Math.Clamp(r * brightness, 0f, 255f);
        g = Math.Clamp(g * brightness, 0f, 255f);
        b = Math.Clamp(b * brightness, 0f, 255f);

        return new Color { R = (byte)r, G = (byte)g, B = (byte)b, A = 255 };
    }



    // -------------------------------------------------
    // Helpers
    // -------------------------------------------------

    private static bool InBounds(int x, int y, int z, int worldHeight)
        => x >= 0 && x < Size &&
           z >= 0 && z < Size &&
           y >= 0 && y < worldHeight;

    private static int Index(int x, int y, int z)
        => x + Size * (z + Size * y);
}
