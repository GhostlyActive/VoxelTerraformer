namespace Terraformer.World;

/// <summary>
/// Bit-Helfer für die Sub-Voxel-Masken des Sculpt-Modus: 8 Teilungen pro Achse
/// = 512 Sub-Voxel pro Block (12,5 cm bei 1-m-Blöcken), als ulong[8] gespeichert.
/// Ein Vollblock entspricht der vollen Maske, Luft der leeren — gespeichert wird
/// eine Maske nur für tatsächlich angeschnitzte Blöcke. Masken sind Copy-on-Write:
/// einmal im Chunk abgelegt, werden sie nie mehr mutiert (Worker-Threads lesen sie).
/// </summary>
public static class SubVoxels
{
    // Divisions muss eine Zweierpotenz bleiben (Shift = log2, LowMask = Divisions-1)
    public const int Divisions = 8;
    public const int Shift = 3;
    public const int LowMask = Divisions - 1;
    public const float CellSize = 1f / Divisions;
    public const int WordCount = Divisions * Divisions * Divisions / 64;

    public static int BitIndex(int sx, int sy, int sz) => sx + Divisions * (sy + Divisions * sz);

    public static bool HasBit(ulong[] mask, int sx, int sy, int sz)
    {
        int bit = BitIndex(sx, sy, sz);
        return (mask[bit >> 6] & (1UL << (bit & 63))) != 0;
    }

    public static void SetBit(ulong[] mask, int sx, int sy, int sz)
    {
        int bit = BitIndex(sx, sy, sz);
        mask[bit >> 6] |= 1UL << (bit & 63);
    }

    public static void ClearBit(ulong[] mask, int sx, int sy, int sz)
    {
        int bit = BitIndex(sx, sy, sz);
        mask[bit >> 6] &= ~(1UL << (bit & 63));
    }

    public static bool IsEmpty(ulong[] mask)
    {
        foreach (ulong word in mask)
            if (word != 0) return false;
        return true;
    }

    public static bool IsFull(ulong[] mask)
    {
        foreach (ulong word in mask)
            if (word != ulong.MaxValue) return false;
        return true;
    }

    public static ulong[] NewEmpty() => new ulong[WordCount];

    public static ulong[] NewFull()
    {
        var mask = new ulong[WordCount];
        Array.Fill(mask, ulong.MaxValue);
        return mask;
    }

    /// <summary>Ist die Randschicht Richtung faceIndex komplett gefüllt? (Face-Reihenfolge des Meshers: +Y,-Y,+X,-X,+Z,-Z; opposite(f) == f ^ 1)</summary>
    public static bool LayerFull(ulong[] mask, int faceIndex)
    {
        ulong[] layer = _faceLayers[faceIndex];
        for (int word = 0; word < WordCount; word++)
            if ((mask[word] & layer[word]) != layer[word]) return false;
        return true;
    }

    private static readonly ulong[][] _faceLayers = BuildFaceLayers();

    private static ulong[][] BuildFaceLayers()
    {
        var layers = new ulong[6][];
        for (int face = 0; face < 6; face++) layers[face] = new ulong[WordCount];

        for (int sz = 0; sz < Divisions; sz++)
        for (int sy = 0; sy < Divisions; sy++)
        for (int sx = 0; sx < Divisions; sx++)
        {
            int bit = BitIndex(sx, sy, sz);
            int word = bit >> 6;
            ulong flag = 1UL << (bit & 63);

            if (sy == Divisions - 1) layers[0][word] |= flag; // +Y
            if (sy == 0) layers[1][word] |= flag;             // -Y
            if (sx == Divisions - 1) layers[2][word] |= flag; // +X
            if (sx == 0) layers[3][word] |= flag;             // -X
            if (sz == Divisions - 1) layers[4][word] |= flag; // +Z
            if (sz == 0) layers[5][word] |= flag;             // -Z
        }

        return layers;
    }
}
