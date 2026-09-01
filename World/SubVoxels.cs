namespace Terraformer.World;

/// <summary>
/// Dichtefeld eines bearbeiteten Blocks: 8 Teilungen pro Achse = 512 Zellen à 12,5 cm,
/// je ein Byte Füllgrad (0 = Luft, 255 = massiv). Ab <see cref="Iso"/> gilt eine Zelle
/// als fest — daran hängen Kollision, Raycast und die kantigen Modi. Marching Cubes nutzt
/// zusätzlich die Zwischenwerte und zieht die Fläche exakt dort, wo der Füllgrad Iso
/// kreuzt; daher die runden Formen im Smooth-Modus.
/// Ein Vollblock entspricht dem vollen Feld, Luft dem leeren — gespeichert wird ein Feld
/// nur für tatsächlich bearbeitete Blöcke. Felder sind Copy-on-Write: einmal im Chunk
/// abgelegt, werden sie nie mehr mutiert (Worker-Threads lesen sie).
/// </summary>
public static class SubVoxels
{
    // Divisions muss eine Zweierpotenz bleiben (Shift = log2, LowMask = Divisions-1)
    public const int Divisions = 8;
    public const int Shift = 3;
    public const int LowMask = Divisions - 1;
    public const float CellSize = 1f / Divisions;
    public const int CellCount = Divisions * Divisions * Divisions;

    /// <summary>Ab diesem Füllgrad ist eine Zelle fest — derselbe Schwellwert, den Marching Cubes als Fläche zieht</summary>
    public const byte Iso = 128;

    /// <summary>
    /// Füllgrade darunter werden auf 0 gerundet. Ohne das bliebe vom weichen Pinselrand
    /// überall ein Hauch Dichte stehen und ausgehöhlte Blöcke würden nie wieder zu Luft.
    /// </summary>
    public const byte Epsilon = 6;

    public static int CellIndex(int sx, int sy, int sz) => sx + Divisions * (sy + Divisions * sz);

    public static byte Get(byte[] field, int sx, int sy, int sz) => field[CellIndex(sx, sy, sz)];

    public static bool IsSolid(byte[] field, int sx, int sy, int sz) => field[CellIndex(sx, sy, sz)] >= Iso;

    public static void Set(byte[] field, int sx, int sy, int sz, float value)
        => field[CellIndex(sx, sy, sz)] = Quantize(value);

    public static byte Quantize(float value)
    {
        if (value < Epsilon) return 0;
        return value >= 255f ? (byte)255 : (byte)(value + 0.5f);
    }

    public static bool IsEmpty(byte[] field)
    {
        foreach (byte cell in field)
            if (cell != 0) return false;
        return true;
    }

    public static bool IsFull(byte[] field)
    {
        foreach (byte cell in field)
            if (cell != 255) return false;
        return true;
    }

    public static byte[] NewEmpty() => new byte[CellCount];

    public static byte[] NewFull()
    {
        var field = new byte[CellCount];
        Array.Fill(field, (byte)255);
        return field;
    }

    /// <summary>Ist die Randschicht Richtung faceIndex durchgehend fest? (Face-Reihenfolge des Meshers: +Y,-Y,+X,-X,+Z,-Z; opposite(f) == f ^ 1)</summary>
    public static bool LayerFull(byte[] field, int faceIndex)
    {
        foreach (int cell in _faceLayers[faceIndex])
            if (field[cell] < Iso) return false;
        return true;
    }

    private static readonly int[][] _faceLayers = BuildFaceLayers();

    private static int[][] BuildFaceLayers()
    {
        var layers = new List<int>[6];
        for (int face = 0; face < 6; face++) layers[face] = new List<int>(Divisions * Divisions);

        for (int sz = 0; sz < Divisions; sz++)
        for (int sy = 0; sy < Divisions; sy++)
        for (int sx = 0; sx < Divisions; sx++)
        {
            int cell = CellIndex(sx, sy, sz);

            if (sy == Divisions - 1) layers[0].Add(cell); // +Y
            if (sy == 0) layers[1].Add(cell);             // -Y
            if (sx == Divisions - 1) layers[2].Add(cell); // +X
            if (sx == 0) layers[3].Add(cell);             // -X
            if (sz == Divisions - 1) layers[4].Add(cell); // +Z
            if (sz == 0) layers[5].Add(cell);             // -Z
        }

        var result = new int[6][];
        for (int face = 0; face < 6; face++) result[face] = layers[face].ToArray();
        return result;
    }
}
