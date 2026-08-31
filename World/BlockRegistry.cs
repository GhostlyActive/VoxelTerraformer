using Raylib_cs;

namespace Terraformer.World;

public readonly record struct BlockDef(
    byte Id,
    string Name,
    bool Solid,
    float Hardness,
    Color BaseColor,
    bool UseHeightGradient,
    float Emissive);

public static class BlockRegistry
{
    public const byte Air = 0;
    public const byte Terrain = 1;
    public const byte Stone = 2;

    private static readonly BlockDef[] _defs = new BlockDef[256];

    static BlockRegistry()
    {
        Register(new BlockDef(Air, "Air", Solid: false, Hardness: 0f, new Color(0, 0, 0, 0), UseHeightGradient: false, Emissive: 0f));

        // Terrain wird nicht über BaseColor eingefärbt, sondern über den Höhenverlauf (TerrainColors)
        Register(new BlockDef(Terrain, "Terrain", Solid: true, Hardness: 1f, new Color(255, 255, 255, 255), UseHeightGradient: true, Emissive: 0f));

        Register(new BlockDef(Stone, "Stone", Solid: true, Hardness: 1.5f, new Color(168, 164, 158, 255), UseHeightGradient: false, Emissive: 0f));
    }

    private static void Register(BlockDef def) => _defs[def.Id] = def;

    public static BlockDef Get(int id) => _defs[(byte)id];

    public static bool IsSolid(int id) => _defs[(byte)id].Solid;
}
