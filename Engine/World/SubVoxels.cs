namespace VoxelEngine.World;

/// <summary>
/// The density field of an edited block: 8 divisions per axis = 512 cells of 12.5 cm, one byte of
/// fill each (0 = air, 255 = solid). From <see cref="Iso"/> upwards a cell counts as solid, which
/// is what collision, raycasting and the hard-edged modes hang off. Marching cubes also uses the
/// values in between and puts the surface exactly where the fill crosses Iso, which is where the
/// rounded shapes in Smooth mode come from.
/// A full block equals a full field and air an empty one, so a field is only stored for blocks
/// that were actually edited. Fields are copy-on-write: once handed to a chunk they are never
/// mutated again, because worker threads read them.
/// </summary>
public static class SubVoxels
{
    // Divisions has to stay a power of two (Shift = log2, LowMask = Divisions-1)
    public const int Divisions = 8;
    public const int Shift = 3;
    public const int LowMask = Divisions - 1;
    public const float CellSize = 1f / Divisions;
    public const int CellCount = Divisions * Divisions * Divisions;

    /// <summary>From this fill upwards a cell is solid: the same threshold marching cubes draws the surface at</summary>
    public const byte Iso = 128;

    /// <summary>
    /// Fills below this are rounded down to 0. Without it, a trace of density from the soft brush
    /// edge would linger everywhere and hollowed-out blocks would never become air again.
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

    /// <summary>Is the border layer towards faceIndex solid all the way? (mesher face order: +Y,-Y,+X,-X,+Z,-Z; opposite(f) == f ^ 1)</summary>
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
