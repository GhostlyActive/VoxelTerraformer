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
/// Alle Voxel-Materialien, nachgeschlagen über ihre Id (das Byte, das im Chunk steht).
/// Die Ids 0..<see cref="FirstGameId"/> gehören der Engine, alles darüber vergibt
/// <see cref="Register"/> an die Spiele. Ein Spiel registriert seine Materialien einmal beim
/// Laden — die Id hängt an der Registrierungsreihenfolge und taugt deshalb nicht für Spielstände
/// von Spielen mit eigenen Materialien.
/// </summary>
public static class BlockRegistry
{
    public const byte Air = 0;
    public const byte Terrain = 1;
    public const byte Stone = 2;

    /// <summary>Ab hier vergibt <see cref="Register"/> — darunter bleibt Platz für Engine-Materialien</summary>
    public const byte FirstGameId = 16;

    private static readonly BlockDef[] _defs = new BlockDef[256];
    private static byte _nextGameId = FirstGameId;

    static BlockRegistry()
    {
        Define(new BlockDef(Air, "Air", Solid: false, Hardness: 0f, new Color(0, 0, 0, 0), UseHeightGradient: false, Emissive: 0f));

        // Terrain wird nicht über BaseColor eingefärbt, sondern über den Höhenverlauf (TerrainColors)
        Define(new BlockDef(Terrain, "Terrain", Solid: true, Hardness: 1f, new Color(255, 255, 255, 255), UseHeightGradient: true, Emissive: 0f));

        Define(new BlockDef(Stone, "Stone", Solid: true, Hardness: 1.5f, new Color(168, 164, 158, 255), UseHeightGradient: false, Emissive: 0f));
    }

    private static void Define(BlockDef def) => _defs[def.Id] = def;

    /// <summary>Neues Material für ein Spiel anlegen; liefert die vergebene Id</summary>
    public static byte Register(string name, Color color, float hardness = 1f, float emissive = 0f)
    {
        if (_nextGameId == 255) throw new InvalidOperationException("Keine freie Block-Id mehr");

        byte id = _nextGameId++;
        Define(new BlockDef(id, name, Solid: true, hardness, color, UseHeightGradient: false, emissive));

        return id;
    }

    public static BlockDef Get(int id) => _defs[(byte)id];

    public static bool IsSolid(int id) => _defs[(byte)id].Solid;
}
