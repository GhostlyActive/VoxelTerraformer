using Raylib_cs;
using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Rendering;

/// <summary>
/// One colour per voxel mode, shared by the HUD indicator, the switch wave and the particle burst
/// so all three read as the same event: warm sand for Blocks, cyan for Sculpt, violet for Smooth.
/// </summary>
public static class ModeColors
{
    public static Color Of(TerrainMode mode) => mode switch
    {
        TerrainMode.Blocks => new Color(240, 200, 110, 255),
        TerrainMode.Sculpt => new Color(95, 225, 235, 255),
        _ => new Color(190, 150, 255, 255),
    };

    /// <summary>The same colour as a 0..1 vector, for shader uniforms</summary>
    public static Vector3 Tint(TerrainMode mode)
    {
        Color color = Of(mode);
        return new Vector3(color.R, color.G, color.B) / 255f;
    }

    public static string Label(TerrainMode mode) => mode switch
    {
        TerrainMode.Blocks => "BLOCKS",
        TerrainMode.Sculpt => "SCULPT",
        _ => "SMOOTH",
    };
}
