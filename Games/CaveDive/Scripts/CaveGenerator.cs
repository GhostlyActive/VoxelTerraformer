using System.Collections.Concurrent;
using System.Numerics;
using VoxelEngine.MathTools;
using VoxelEngine.World;

namespace Games.CaveDive;

/// <summary>
/// Solid rock threaded with the engine's cave network, and crystals growing on the cave walls:
/// wherever a tunnel touches rock, a hash decides whether a glowing block sits there. The
/// positions are remembered per chunk so the game can tell which ones the player has chipped off.
/// Runs on the engine's worker threads, so it keeps no state beyond that thread-safe record.
/// </summary>
public sealed class CaveGenerator : ITerrainGenerator
{
    private readonly DefaultTerrainGenerator _rock;
    private readonly byte _crystal;
    private readonly int _seed;

    /// <summary>One crystal per this many wall blocks, roughly</summary>
    private const int WallBlocksPerCrystal = 260;

    /// <summary>Crystal positions by chunk, filled as chunks are generated</summary>
    public ConcurrentDictionary<ChunkCoord, List<Vector3>> Crystals { get; } = new();

    public CaveGenerator(byte crystal, int seed)
    {
        _crystal = crystal;
        _seed = seed;

        // A high, gentle surface over deep rock: the caves reach all the way down and stay put
        _rock = new DefaultTerrainGenerator
        {
            Seed = seed,
            BaseHeight = 40,
            ContinentAmplitude = 8f,
            MountainAmplitude = 4f,
            DetailAmplitude = 2f,
            Spires = 0f,
            Caves = 1.25f,
            CaveDepth = 400,
            CaveRoof = 6,
        };
    }

    public void Generate(ChunkCoord coord, byte[] blocks, int worldHeight)
    {
        _rock.Generate(coord, blocks, worldHeight);

        var found = new List<Vector3>();

        for (int y = 3; y < worldHeight - 1; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int index = Chunk.Index(x, y, z);
            if (BlockRegistry.IsSolid(blocks[index])) continue;

            // A wall: air with rock beside or below it, inside the chunk so the crystal is ours
            bool wall =
                (x > 0 && BlockRegistry.IsSolid(blocks[Chunk.Index(x - 1, y, z)])) ||
                (x < Chunk.Size - 1 && BlockRegistry.IsSolid(blocks[Chunk.Index(x + 1, y, z)])) ||
                (z > 0 && BlockRegistry.IsSolid(blocks[Chunk.Index(x, y, z - 1)])) ||
                (z < Chunk.Size - 1 && BlockRegistry.IsSolid(blocks[Chunk.Index(x, y, z + 1)])) ||
                BlockRegistry.IsSolid(blocks[Chunk.Index(x, y - 1, z)]);
            if (!wall) continue;

            int worldX = coord.X * Chunk.Size + x;
            int worldZ = coord.Z * Chunk.Size + z;
            if (Noise.Value3D(worldX * 0.91f, y * 0.91f, worldZ * 0.91f, _seed + 4242) * WallBlocksPerCrystal >= 1f) continue;

            blocks[index] = _crystal;
            found.Add(new Vector3(worldX + 0.5f, y + 0.5f, worldZ + 0.5f));
        }

        Crystals[coord] = found;
    }
}
