namespace Terraformer.World;

public readonly struct ChunkCoord
{
    public readonly int X;
    public readonly int Z;

    public ChunkCoord(int x, int z)
    {
        X = x;
        Z = z;
    }

    public override int GetHashCode() => HashCode.Combine(X, Z);

    public override bool Equals(object? obj)
        => obj is ChunkCoord other && other.X == X && other.Z == Z;

    public override string ToString() => $"Chunk({X},{Z})";
}
