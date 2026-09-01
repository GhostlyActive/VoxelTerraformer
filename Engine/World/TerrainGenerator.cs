using VoxelEngine.MathTools;

namespace VoxelEngine.World;

/// <summary>
/// Füllt einen frisch erzeugten Chunk mit Gelände. Bekommt nur seine Koordinate und das rohe
/// Blockarray — kein Weltzugriff, damit jede Chunk-Koordinate unabhängig und in beliebiger
/// Reihenfolge erzeugt werden kann. Index über <see cref="Chunk.Index"/>.
/// </summary>
public interface ITerrainGenerator
{
    void Generate(ChunkCoord coord, byte[] blocks, int worldHeight);
}

/// <summary>
/// Standardgelände: große Kontinentform, darüber Bergketten aus Ridged Noise und ein
/// Feindetail-Rauschen. Alle Amplituden sind Blockhöhen, alle Scales Frequenzen pro Block.
/// </summary>
public sealed class DefaultTerrainGenerator : ITerrainGenerator
{
    public int Seed { get; init; } = 1337;

    /// <summary>Grundhöhe, unter der immer Material steht ("Meeresspiegel")</summary>
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

            // Anheben der Kontinentkurve: echte Tiefebenen und echtes Hochland statt überall Hügel
            float shaped = MathF.Pow(continents, 2.1f);

            // Berge stehen im Hochland, nicht in den Ebenen
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
