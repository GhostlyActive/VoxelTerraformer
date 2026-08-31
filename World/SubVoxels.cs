namespace Terraformer.World;

/// <summary>
/// Bit-Helfer für die 4x4x4-Sub-Voxel-Masken des Sculpt-Modus.
/// Ein Vollblock entspricht der vollen Maske, Luft der leeren — gespeichert wird
/// eine Maske nur für tatsächlich angeschnitzte Blöcke.
/// </summary>
public static class SubVoxels
{
    public const int Divisions = 4;
    public const float CellSize = 1f / Divisions;

    public static int BitIndex(int sx, int sy, int sz) => sx + Divisions * (sy + Divisions * sz);

    public static bool HasBit(ulong mask, int sx, int sy, int sz)
        => (mask & (1UL << BitIndex(sx, sy, sz))) != 0;

    /// <summary>
    /// Randschicht-Masken in der Face-Reihenfolge des Meshers (+Y, -Y, +X, -X, +Z, -Z).
    /// Gegenüberliegende Seiten liegen nebeneinander: opposite(f) == f ^ 1.
    /// </summary>
    public static readonly ulong[] FaceLayers = BuildFaceLayers();

    private static ulong[] BuildFaceLayers()
    {
        var layers = new ulong[6];

        for (int sz = 0; sz < Divisions; sz++)
        for (int sy = 0; sy < Divisions; sy++)
        for (int sx = 0; sx < Divisions; sx++)
        {
            ulong bit = 1UL << BitIndex(sx, sy, sz);
            if (sy == Divisions - 1) layers[0] |= bit; // +Y
            if (sy == 0) layers[1] |= bit;             // -Y
            if (sx == Divisions - 1) layers[2] |= bit; // +X
            if (sx == 0) layers[3] |= bit;             // -X
            if (sz == Divisions - 1) layers[4] |= bit; // +Z
            if (sz == 0) layers[5] |= bit;             // -Z
        }

        return layers;
    }
}
