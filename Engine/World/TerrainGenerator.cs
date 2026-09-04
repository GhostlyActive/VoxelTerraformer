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
/// The surface is a height per column, but the world is not: caves are carved out of the rock
/// below it and spires grow out of it, so the result has tunnels, arches and overhangs rather than
/// a single skin over solid ground. Set <see cref="Caves"/> and <see cref="Spires"/> to 0 for the
/// plain heightmap.
///
/// All amplitudes are block heights for a world 64 blocks high, all scales frequencies per
/// block. A taller world stretches the amplitudes with its height and widens the features by the
/// square root of that, so a 256-block world gets mountains four times as tall and twice as wide,
/// and caves all the way down. The mesa and canyon strengths follow
/// <see cref="ContinentAmplitude"/>, so a flatter preset stays flat everywhere instead of tearing
/// gorges into a gentle landscape.
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

    /// <summary>
    /// How much rock the cave network takes out, 0..1. Tunnels appear where two noise fields are
    /// both near their middle — the intersection of two sheets is a tube, which is what gives
    /// winding passages instead of blobs.
    /// </summary>
    public float Caves { get; init; } = 1f;

    public float CaveScale { get; init; } = 0.055f;

    /// <summary>
    /// Blocks below the surface the cave network reaches. Deeper rock stays solid, which is what
    /// keeps a tall world in memory: a slab of pure rock costs nothing, a slab with tunnels 32 KB.
    /// </summary>
    public int CaveDepth { get; init; } = 120;

    /// <summary>
    /// Blocks of rock left between a tunnel and the surface, so the ground is not open everywhere.
    /// It thins out in patches, which is what puts cave mouths and sinkholes into the landscape —
    /// a cave with no way in might as well not be there.
    /// </summary>
    public int CaveRoof { get; init; } = 5;

    public float CaveEntranceScale { get; init; } = 0.01f;

    /// <summary>How much rock stands up out of the surface as towers and arches, 0..1</summary>
    public float Spires { get; init; } = 1f;

    public float SpireScale { get; init; } = 0.07f;

    /// <summary>Blocks a spire may reach above the surface</summary>
    public int SpireHeight { get; init; } = 12;

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

        float vertical = worldHeight / 64f;
        float horizontal = 1f / MathF.Sqrt(vertical);
        int spireHeight = (int)(SpireHeight * MathF.Sqrt(vertical));

        for (int x = 0; x < Chunk.Size; x++)
        for (int z = 0; z < Chunk.Size; z++)
        {
            int worldX = originX + x;
            int worldZ = originZ + z;

            int surface = Math.Clamp(HeightAt(worldX, worldZ, vertical, horizontal), 1, worldHeight - 2);
            int top = Spires > 0f ? Math.Min(worldHeight - 2, surface + spireHeight) : surface;

            // Spires only grow on top of something, so they read as towers and arches rather than
            // blocks hanging in the air
            bool belowSolid = true;

            for (int y = 0; y <= top; y++)
            {
                bool solid = y <= surface
                    ? !IsCave(worldX, y, worldZ, surface)
                    : belowSolid && IsSpire(worldX, y, worldZ, surface, spireHeight);

                if (solid) blocks[Chunk.Index(x, y, z)] = Block;
                belowSolid = solid;
            }
        }
    }

    /// <summary>Terrain height at a world column, before clamping</summary>
    private int HeightAt(int worldX, int worldZ, float vertical, float horizontal)
    {
        // Domain warping: sampling a displaced position bends coastlines and ridges into
        // meandering shapes instead of the smooth blobs plain fBm produces
        float warpX = Noise.Value2D(worldX * WarpScale * horizontal, worldZ * WarpScale * horizontal, Seed + 555) - 0.5f;
        float warpZ = Noise.Value2D(worldX * WarpScale * horizontal + 31f, worldZ * WarpScale * horizontal - 17f, Seed + 556) - 0.5f;

        float sampleX = (worldX + warpX * WarpStrength / horizontal) * horizontal;
        float sampleZ = (worldZ + warpZ * WarpStrength / horizontal) * horizontal;

        // The character of the land, changing over hundreds of blocks
        float region = Variety <= 0f
            ? 0.5f
            : Noise.Fbm2D(worldX * RegionScale * horizontal, worldZ * RegionScale * horizontal, Seed + 3000, 3, 0.5f, 2f);

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
            BaseHeight * vertical +
            shaped * ContinentAmplitude * vertical +
            mountains * MountainAmplitude * vertical * mountainMask +
            (detail - 0.5f) * 2f * DetailAmplitude * vertical;

        if (Variety > 0f)
        {
            height = ApplyTerraces(height, region, TerraceStep * vertical);
            height -= CanyonDepth(sampleX, sampleZ, region) * vertical;
        }

        return (int)MathF.Floor(height);
    }

    /// <summary>
    /// In a narrow band of region values the height snaps to steps, which turns rolling hills into
    /// layered mesas. Blended in and out so the terraces do not start at a visible seam.
    /// </summary>
    private static float ApplyTerraces(float height, float region, float step)
    {
        float mesa =
            Noise.SmoothStep(0.30f, 0.42f, region) *
            (1f - Noise.SmoothStep(0.42f, 0.56f, region));

        if (mesa <= 0.01f) return height;

        float terraced = MathF.Round(height / step) * step;
        return height + (terraced - height) * mesa;
    }

    /// <summary>
    /// A voxel sits in a tunnel when both noise fields are close to their middle. The first test
    /// rejects the vast majority, so the second sample is only paid for near a cave.
    /// </summary>
    private bool IsCave(int x, int y, int z, int surface)
    {
        if (Caves <= 0f) return false;
        if (y < 2 || y < surface - CaveDepth || y > surface - RoofAt(x, z)) return false; // bedrock, the deep rock and a roof stay solid

        float width = 0.075f * Caves;

        // The vertical scale is doubled so passages come out wider than they are tall
        float first = Noise.Value3D(x * CaveScale, y * CaveScale * 2f, z * CaveScale, Seed + 5100);
        if (MathF.Abs(first - 0.5f) > width) return false;

        float second = Noise.Value3D(x * CaveScale, y * CaveScale * 2f, z * CaveScale, Seed + 5200);
        return MathF.Abs(second - 0.5f) <= width;
    }

    /// <summary>
    /// How much rock covers the tunnels here. Zero in patches, which is where a passage breaks
    /// through and becomes an entrance.
    /// </summary>
    private int RoofAt(int x, int z)
    {
        float opening = Noise.Value2D(x * CaveEntranceScale, z * CaveEntranceScale, Seed + 5300);

        return (int)MathF.Round(CaveRoof * (1f - Noise.SmoothStep(0.60f, 0.84f, opening)));
    }

    /// <summary>Rock standing above the surface; the threshold rises with height so towers taper</summary>
    private bool IsSpire(int x, int y, int z, int surface, int spireHeight)
    {
        float above = (y - surface) / (float)Math.Max(1, spireHeight);
        float threshold = 0.56f + above * 0.34f;

        return Noise.Value3D(x * SpireScale, y * SpireScale * 1.5f, z * SpireScale, Seed + 6100)
               > threshold / MathF.Max(0.001f, Spires);
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
