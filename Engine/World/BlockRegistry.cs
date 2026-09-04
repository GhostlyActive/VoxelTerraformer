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

    /// <summary>
    /// Add a material for a game; returns the id it was given. Registering the same name with the
    /// same values again hands back the existing id, so a game may register in <see cref="Core.Game.Load"/>
    /// every time it starts. The same name with different values is a clash and throws.
    /// Only call it from Load: the mesh workers of a running scene read the definitions.
    /// </summary>
    public static byte Register(string name, Color color, float hardness = 1f, float emissive = 0f)
    {
        for (int id = FirstGameId; id < _nextGameId; id++)
        {
            BlockDef existing = _defs[id];
            if (existing.Name != name) continue;

            bool same = existing.BaseColor.Equals(color) && existing.Hardness == hardness && existing.Emissive == emissive;
            if (same) return (byte)id;

            throw new InvalidOperationException($"Block '{name}' is already registered with different values");
        }

        if (_nextGameId == 255) throw new InvalidOperationException("No free block id left");

        byte fresh = _nextGameId++;
        Define(new BlockDef(fresh, name, Solid: true, hardness, color, UseHeightGradient: false, emissive));

        return fresh;
    }

    public static BlockDef Get(int id) => _defs[(byte)id];

    public static bool IsSolid(int id) => _defs[(byte)id].Solid;
}
