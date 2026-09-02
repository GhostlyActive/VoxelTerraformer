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
/// The default terrain. Height comes from a large continental shape with ridged mountain ranges
/// and a fine detail layer on top, but the character of the land changes from place to place: a
/// slow region noise decides where you get plains, mountains, stepped mesas or canyons, and the
/// sample position itself is warped so nothing runs in straight noise-shaped bands.
///
/// All amplitudes are block heights, all scales frequencies per block. The mesa and canyon
/// strengths follow <see cref="ContinentAmplitude"/>, so a flatter preset stays flat everywhere
/// instead of tearing gorges into a gentle landscape.
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

    /// <summary>How far the sample position is pushed around, in blocks. 0 gives plain noise.</summary>
    public float WarpStrength { get; init; } = 80f;

    /// <summary>0 turns off regions entirely and gives one uniform landscape everywhere</summary>
    public float Variety { get; init; } = 1f;

    public byte Block { get; init; } = BlockRegistry.Terrain;

    /// <summary>Size of the steps in mesa country, in blocks</summary>
    private const float TerraceStep = 5f;

    private const float RegionScale = 0.0035f;
    private const float WarpScale = 0.006f;
    private const float CanyonScale = 0.02f;

    public void Generate(ChunkCoord coord, byte[] blocks, int worldHeight)
    {
        int originX = coord.X * Chunk.Size;
        int originZ = coord.Z * Chunk.Size;

        for (int x = 0; x < Chunk.Size; x++)
        for (int z = 0; z < Chunk.Size; z++)
        {
            int worldX = originX + x;
            int worldZ = originZ + z;

            int top = Math.Clamp(HeightAt(worldX, worldZ), 1, worldHeight - 2);

            for (int y = 0; y <= top; y++)
                blocks[Chunk.Index(x, y, z)] = Block;
        }
    }

    /// <summary>Terrain height at a world column, before clamping</summary>
    private int HeightAt(int worldX, int worldZ)
    {
        // Domain warping: sampling a displaced position bends coastlines and ridges into
        // meandering shapes instead of the smooth blobs plain fBm produces
        float warpX = Noise.Value2D(worldX * WarpScale, worldZ * WarpScale, Seed + 555) - 0.5f;
        float warpZ = Noise.Value2D(worldX * WarpScale + 31f, worldZ * WarpScale - 17f, Seed + 556) - 0.5f;

        float sampleX = worldX + warpX * WarpStrength;
        float sampleZ = worldZ + warpZ * WarpStrength;

        // The character of the land, changing over hundreds of blocks
        float region = Variety <= 0f
            ? 0.5f
            : Noise.Fbm2D(worldX * RegionScale, worldZ * RegionScale, Seed + 3000, 3, 0.5f, 2f);

        float continents = Noise.Fbm2D(sampleX * ContinentScale, sampleZ * ContinentScale, Seed, 4, 0.5f, 2f);
        float mountains = Noise.RidgeFbm2D(sampleX * MountainScale, sampleZ * MountainScale, Seed + 9000, 5, 0.5f, 2f);
        float detail = Noise.Fbm2D(sampleX * DetailScale, sampleZ * DetailScale, Seed + 42000, 3, 0.55f, 2f);

        // Bending the continental curve: real lowlands and real highlands instead of hills everywhere
        float shaped = MathF.Pow(continents, 2.1f);

        // Mountains need both high ground and a mountainous region to stand in
        float mountainMask =
            Noise.SmoothStep(0.35f, 0.70f, shaped) *
            Noise.SmoothStep(0.42f, 0.78f, region);

        float height =
            BaseHeight +
            shaped * ContinentAmplitude +
            mountains * MountainAmplitude * mountainMask +
            (detail - 0.5f) * 2f * DetailAmplitude;

        if (Variety > 0f)
        {
            height = ApplyTerraces(height, region);
            height -= CanyonDepth(sampleX, sampleZ, region);
        }

        return (int)MathF.Floor(height);
    }

    /// <summary>
    /// In a narrow band of region values the height snaps to steps, which turns rolling hills into
    /// layered mesas. Blended in and out so the terraces do not start at a visible seam.
    /// </summary>
    private static float ApplyTerraces(float height, float region)
    {
        float mesa =
            Noise.SmoothStep(0.30f, 0.42f, region) *
            (1f - Noise.SmoothStep(0.42f, 0.56f, region));

        if (mesa <= 0.01f) return height;

        float terraced = MathF.Round(height / TerraceStep) * TerraceStep;
        return height + (terraced - height) * mesa;
    }

    /// <summary>Ridged noise cutting gorges into the low, dry regions</summary>
    private float CanyonDepth(float sampleX, float sampleZ, float region)
    {
        float mask = 1f - Noise.SmoothStep(0.12f, 0.32f, region);
        if (mask <= 0.01f) return 0f;

        float ridge = Noise.RidgeFbm2D(sampleX * CanyonScale, sampleZ * CanyonScale, Seed + 7777, 3, 0.5f, 2f);
        float cut = Noise.SmoothStep(0.70f, 0.95f, ridge);

        return cut * mask * ContinentAmplitude * 0.55f;
    }
}
