using Raylib_cs;

namespace VoxelEngine.World;

public readonly record struct BlockDef(
    byte Id,
    string Name,
    bool Solid,
    float Hardness,
    Color BaseColor,
    bool UseHeightGradient,
    float Emissive);

/// <summary>
/// Every voxel material, looked up by its id (the byte stored in the chunk). Ids
/// 0..<see cref="FirstGameId"/> belong to the engine, everything above is handed out to games by
/// <see cref="Register"/>. A game registers its materials once while loading; the id depends on the
/// order of registration and is therefore not suitable for saves of games with their own
/// materials.
/// </summary>
public static class BlockRegistry
{
    public const byte Air = 0;
    public const byte Terrain = 1;
    public const byte Stone = 2;

    /// <summary>Where <see cref="Register"/> starts handing out ids; below it stays room for engine materials</summary>
    public const byte FirstGameId = 16;

    private static readonly BlockDef[] _defs = new BlockDef[256];
    private static byte _nextGameId = FirstGameId;

    static BlockRegistry()
    {
        Define(new BlockDef(Air, "Air", Solid: false, Hardness: 0f, new Color(0, 0, 0, 0), UseHeightGradient: false, Emissive: 0f));

        // Terrain is not coloured from BaseColor but from the height gradient (TerrainColors)
        Define(new BlockDef(Terrain, "Terrain", Solid: true, Hardness: 1f, new Color(255, 255, 255, 255), UseHeightGradient: true, Emissive: 0f));

        Define(new BlockDef(Stone, "Stone", Solid: true, Hardness: 1.5f, new Color(168, 164, 158, 255), UseHeightGradient: false, Emissive: 0f));
    }

    private static void Define(BlockDef def) => _defs[def.Id] = def;

    /// <summary>Add a new material for a game; returns the id it was given</summary>
    public static byte Register(string name, Color color, float hardness = 1f, float emissive = 0f)
    {
        if (_nextGameId == 255) throw new InvalidOperationException("No free block id left");

        byte id = _nextGameId++;
        Define(new BlockDef(id, name, Solid: true, hardness, color, UseHeightGradient: false, emissive));

        return id;
    }

    public static BlockDef Get(int id) => _defs[(byte)id];

    public static bool IsSolid(int id) => _defs[(byte)id].Solid;
}
