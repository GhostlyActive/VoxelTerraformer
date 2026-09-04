namespace VoxelEngine.Rendering;

/// <summary>
/// Which level of detail a chunk column gets, by its distance from the camera in chunks. Level 0
/// is full detail, level 1 meshes 2 m blocks, level 2 meshes 4 m blocks. The bands overlap by one
/// chunk of hysteresis in each direction, so a column standing right on a border does not flip
/// back and forth, and with it remesh, every time the player takes a step.
/// </summary>
public static class LodPolicy
{
    public const int Levels = 3;

    /// <summary>Metres per block at a level</summary>
    public static int ScaleOf(int lod) => 1 << lod;

    /// <summary>Radius of the middle band as a multiple of the detail radius</summary>
    private const float MidBandFactor = 3f;

    /// <summary>
    /// The level a column should have, given the one it has now (-1 for none yet), its distance
    /// from the camera in chunks and the radius of full detail.
    /// </summary>
    public static int Choose(int current, float distance, float detailRadius)
    {
        float detailLimit = MathF.Max(1f, detailRadius);
        float midLimit = detailLimit * MidBandFactor;

        int wanted = distance <= detailLimit ? 0 : distance <= midLimit ? 1 : 2;

        if (current < 0 || wanted == current) return wanted;

        // Coarser only once clearly past the band, finer only once clearly inside it
        if (wanted > current)
            return distance > Limit(current, detailLimit, midLimit) + 1f ? wanted : current;

        return distance <= Limit(wanted, detailLimit, midLimit) - 1f ? wanted : current;
    }

    private static float Limit(int level, float detailLimit, float midLimit)
        => level == 0 ? detailLimit : midLimit;
}
