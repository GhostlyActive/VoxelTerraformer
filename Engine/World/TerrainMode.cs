namespace VoxelEngine.World;

/// <summary>
/// The three voxel modes. All of them work on the same world data, so switching is lossless:
/// only the tool and the look change, never the stored geometry.
/// </summary>
public enum TerrainMode
{
    /// <summary>Place and remove whole blocks, hard-edged look</summary>
    Blocks,

    /// <summary>Sphere brush at sub-voxel level, hard-edged look</summary>
    Sculpt,

    /// <summary>Sphere brush at sub-voxel level, smoothed marching-cubes surface</summary>
    Smooth,
}
