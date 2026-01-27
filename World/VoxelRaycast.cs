using System.Numerics;

namespace Terraformer;

public static class VoxelRaycast
{
    public struct Hit
    {
        public bool HasHit;
        public Vector3Int Block;       // the voxel that was hit
        public Vector3Int PlaceBlock;  // adjacent voxel to place into
        public Vector3 Normal;         // face normal of the hit
    }

    // Small int vector helper
    public readonly struct Vector3Int
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public Vector3Int(int x, int y, int z) { X = x; Y = y; Z = z; }

        public static Vector3Int operator +(Vector3Int a, Vector3Int b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3Int operator -(Vector3Int a, Vector3Int b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public override string ToString() => $"({X},{Y},{Z})";
    }

    public static Hit Cast(
        Func<int, int, int, int> getBlock, // returns block id (0 = air)
        Vector3 origin,
        Vector3 direction,
        float maxDistance)
    {
        direction = Vector3.Normalize(direction);

        // Start voxel
        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        // Step direction
        int stepX = direction.X >= 0 ? 1 : -1;
        int stepY = direction.Y >= 0 ? 1 : -1;
        int stepZ = direction.Z >= 0 ? 1 : -1;

        // Compute tMax and tDelta
        float tDeltaX = direction.X == 0 ? float.PositiveInfinity : MathF.Abs(1f / direction.X);
        float tDeltaY = direction.Y == 0 ? float.PositiveInfinity : MathF.Abs(1f / direction.Y);
        float tDeltaZ = direction.Z == 0 ? float.PositiveInfinity : MathF.Abs(1f / direction.Z);

        float nextVoxelBoundaryX = (direction.X >= 0) ? (x + 1) : x;
        float nextVoxelBoundaryY = (direction.Y >= 0) ? (y + 1) : y;
        float nextVoxelBoundaryZ = (direction.Z >= 0) ? (z + 1) : z;

        float tMaxX = direction.X == 0 ? float.PositiveInfinity : MathF.Abs((nextVoxelBoundaryX - origin.X) / direction.X);
        float tMaxY = direction.Y == 0 ? float.PositiveInfinity : MathF.Abs((nextVoxelBoundaryY - origin.Y) / direction.Y);
        float tMaxZ = direction.Z == 0 ? float.PositiveInfinity : MathF.Abs((nextVoxelBoundaryZ - origin.Z) / direction.Z);

        Vector3 lastNormal = Vector3.Zero;
        float t = 0f;

        while (t <= maxDistance)
        {
            int id = getBlock(x, y, z);
            if (id != 0)
            {
                var hitBlock = new Vector3Int(x, y, z);
                var placeBlock = new Vector3Int(x, y, z) + NormalToInt(lastNormal);

                return new Hit
                {
                    HasHit = true,
                    Block = hitBlock,
                    PlaceBlock = placeBlock,
                    Normal = lastNormal
                };
            }

            // Step to next cell
            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX;
                t = tMaxX;
                tMaxX += tDeltaX;
                lastNormal = new Vector3(-stepX, 0, 0);
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY;
                t = tMaxY;
                tMaxY += tDeltaY;
                lastNormal = new Vector3(0, -stepY, 0);
            }
            else
            {
                z += stepZ;
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                lastNormal = new Vector3(0, 0, -stepZ);
            }
        }

        return new Hit { HasHit = false };
    }

    private static Vector3Int NormalToInt(Vector3 n)
    {
        // Normal is always axis-aligned and integer-ish here
        return new Vector3Int((int)n.X, (int)n.Y, (int)n.Z);
    }
}
