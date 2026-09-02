using VoxelEngine.MathTools;

namespace VoxelEngine.World;

/// <summary>
/// Fills a freshly created chunk with terrain. Gets only its coordinate and the raw block array,
/// with no access to the world, so that any chunk coordinate can be generated independently and in
/// any order. Index through <see cref="Chunk.Index"/>.
/// </summary>
public interface ITerrainGenerator
{
    void Generate(ChunkCoord coord, byte[] blocks, int worldHeight);
}

/// <summary>
/// The default terrain: a large continental shape, mountain ranges from ridged noise on top of it
/// and a fine detail layer. All amplitudes are block heights, all scales frequencies per block.
/// </summary>
public sealed class DefaultTerrainGenerator : ITerrainGenerator
{
    public int Seed { get; init; } = 1337;

    /// <summary>Base height with solid material below it throughout ("sea level")</summary>
    public int BaseHeight { get; init; } = 1;

    public float ContinentScale { get; init; } = 0.012f;
    public float ContinentAmplitude { get; init; } = 24f;

    public float MountainScale { get; init; } = 0.035f;
    public float MountainAmplitude { get; init; } = 26f;

    public float DetailScale { get; init; } = 0.09f;
    public float DetailAmplitude { get; init; } = 5f;

    public byte Block { get; init; } = BlockRegistry.Terrain;

    public void Generate(ChunkCoord coord, byte[] blocks, int worldHeight)
    {
        int originX = coord.X * Chunk.Size;
        int originZ = coord.Z * Chunk.Size;

        for (int x = 0; x < Chunk.Size; x++)
        for (int z = 0; z < Chunk.Size; z++)
        {
            int worldX = originX + x;
            int worldZ = originZ + z;

            float continents = Noise.Fbm2D(worldX * ContinentScale, worldZ * ContinentScale, Seed, 4, 0.5f, 2.0f);
            float mountains = Noise.RidgeFbm2D(worldX * MountainScale, worldZ * MountainScale, Seed + 9000, 5, 0.5f, 2.0f);
            float detail = Noise.Fbm2D(worldX * DetailScale, worldZ * DetailScale, Seed + 42000, 3, 0.55f, 2.0f);

            // Bending the continental curve: real lowlands and real highlands instead of hills everywhere
            float shaped = MathF.Pow(continents, 2.1f);

            // Mountains belong in the highlands, not on the plains
            float mountainMask = Noise.SmoothStep(0.45f, 0.75f, shaped);

            float height =
                BaseHeight +
                shaped * ContinentAmplitude +
                mountains * MountainAmplitude * mountainMask +
                (detail - 0.5f) * 2f * DetailAmplitude;

            int top = Math.Clamp((int)MathF.Floor(height), 1, worldHeight - 2);

            for (int y = 0; y <= top; y++)
                blocks[Chunk.Index(x, y, z)] = Block;
        }
    }
}
