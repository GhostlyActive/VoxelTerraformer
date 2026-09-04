using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// The cheap question both meshers ask before walking a section: can a surface run through it at
/// all? A section of nothing but air, or of nothing but full blocks, has none, and that is most of
/// the sky and all of the deep rock. The rows above and below count too, because a block reads its
/// neighbours.
/// </summary>
public static class MeshSectionScan
{
    public static bool CanHaveSurface(in MeshGrid grid, Dictionary<int, byte[]> refinements, int yStart, int yEnd)
    {
        byte[] padded = grid.Blocks;
        int first = grid.Index(-1, Math.Max(-1, yStart - 1), -1);
        int last = grid.Index(grid.Size, Math.Min(grid.Height, yEnd), grid.Size);

        bool anySolid = false;
        bool anyOpen = false;

        for (int index = first; index <= last; index++)
        {
            if (BlockRegistry.IsSolid(padded[index])) anySolid = true;
            else anyOpen = true;

            if (anySolid && anyOpen) return true;
        }

        if (!anySolid) return false;

        // Solid throughout, but a carved block within reach still holds empty space
        foreach (int index in refinements.Keys)
            if (index >= first && index <= last) return true;

        return false;
    }
}
