using System.Numerics;

namespace Terraformer.World;

public class Chunk
{
    public const int Size = 32;

    // Layout: x + Size * (z + Size * y)
    private readonly byte[] _blocks;

    public readonly ChunkCoord Coord;
    public readonly Vector3 WorldPosition;

    /// <summary>True, sobald der Chunk seit Generierung/Laden verändert wurde → muss gespeichert werden</summary>
    public bool Modified { get; private set; }

    public Chunk(ChunkCoord coord, int worldHeight, byte[]? loadedBlocks = null)
    {
        Coord = coord;
        WorldPosition = new Vector3(coord.X * Size, 0, coord.Z * Size);

        if (loadedBlocks != null)
        {
            _blocks = loadedBlocks;
        }
        else
        {
            _blocks = new byte[Size * worldHeight * Size];
            GenerateTerrain(worldHeight);
        }
    }

    // Direkter Zugriff nur für Speichern/Laden — nicht aus Gameplay-Code verwenden
    internal byte[] RawBlocks => _blocks;

    internal void MarkSaved() => Modified = false;

    public int GetLocal(int x, int y, int z, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return 0;
        return _blocks[Index(x, y, z)];
    }

    public void SetLocal(int x, int y, int z, int id, int worldHeight)
    {
        if (!InBounds(x, y, z, worldHeight)) return;
        _blocks[Index(x, y, z)] = (byte)Math.Clamp(id, 0, 255);
        Modified = true;
    }

    // Kopiert eine komplette X-Zeile am Stück (für den Mesh-Snapshot)
    public void CopyRow(int y, int z, byte[] destination, int destinationIndex)
        => Array.Copy(_blocks, Index(0, y, z), destination, destinationIndex, Size);

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

        const int baseHeight = 1;             // "Meeresspiegel / Grund"

        for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
            {
                int wx = (int)WorldPosition.X + x;
                int wz = (int)WorldPosition.Z + z;

                // 0..1
                float continents = Noise.Fbm2D(wx * continentScale, wz * continentScale, seed, continentOct, 0.5f, 2.0f);
                float mountains = Noise.RidgeFbm2D(wx * mountainScale, wz * mountainScale, seed + 9000, mountainOct, 0.5f, 2.0f);
                float detail = Noise.Fbm2D(wx * detailScale, wz * detailScale, seed + 42000, detailOct, 0.55f, 2.0f);

                // Kontinente "shapen": mehr echte Lowlands + echte Highlands
                float contShaped = MathF.Pow(continents, 2.1f);

                // Mountain-Maske: Berge eher im "Hochland", weniger in tiefen Ebenen
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
                    _blocks[Index(x, y, z)] = BlockRegistry.Terrain;
            }
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
