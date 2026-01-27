using System.Numerics;

namespace Terraformer.MathTools;

public static class VoxelRaycast
{
    public readonly struct Vector3Int
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public Vector3Int(int x, int y, int z) { X = x; Y = y; Z = z; }

        public static Vector3Int operator +(Vector3Int a, Vector3Int b)
            => new Vector3Int(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public override string ToString() => $"({X},{Y},{Z})";
    }

    public struct Hit
    {
        public bool HasHit;
        public Vector3Int Block;      // The solid block that was hit
        public Vector3Int PlaceBlock; // Neighbor cell to place into
        public Vector3 Normal;        // Face normal of the hit
        public float Distance;
    }

    /// <summary>
    /// Fast voxel traversal using 3D-DDA. No small step loops needed.
    /// getBlock must return 0 for air.
    /// </summary>
    public static Hit Cast(
        System.Func<int, int, int, int> getBlock,
        Vector3 origin,
        Vector3 direction,
        float maxDistance)
    {
        if (direction.LengthSquared() < 1e-8f)
            return new Hit { HasHit = false };

        direction = Vector3.Normalize(direction);

        int x = (int)System.MathF.Floor(origin.X);
        int y = (int)System.MathF.Floor(origin.Y);
        int z = (int)System.MathF.Floor(origin.Z);

        int stepX = direction.X >= 0 ? 1 : -1;
        int stepY = direction.Y >= 0 ? 1 : -1;
        int stepZ = direction.Z >= 0 ? 1 : -1;

        float tDeltaX = direction.X == 0 ? float.PositiveInfinity : System.MathF.Abs(1f / direction.X);
        float tDeltaY = direction.Y == 0 ? float.PositiveInfinity : System.MathF.Abs(1f / direction.Y);
        float tDeltaZ = direction.Z == 0 ? float.PositiveInfinity : System.MathF.Abs(1f / direction.Z);

        float nextBoundaryX = direction.X >= 0 ? (x + 1) : x;
        float nextBoundaryY = direction.Y >= 0 ? (y + 1) : y;
        float nextBoundaryZ = direction.Z >= 0 ? (z + 1) : z;

        float tMaxX = direction.X == 0 ? float.PositiveInfinity : System.MathF.Abs((nextBoundaryX - origin.X) / direction.X);
        float tMaxY = direction.Y == 0 ? float.PositiveInfinity : System.MathF.Abs((nextBoundaryY - origin.Y) / direction.Y);
        float tMaxZ = direction.Z == 0 ? float.PositiveInfinity : System.MathF.Abs((nextBoundaryZ - origin.Z) / direction.Z);

        Vector3 lastNormal = Vector3.Zero;
        float t = 0f;

        while (t <= maxDistance)
        {
            if (getBlock(x, y, z) != 0)
            {
                var hitBlock = new Vector3Int(x, y, z);
                var placeBlock = hitBlock + NormalToInt(lastNormal);

                return new Hit
                {
                    HasHit = true,
                    Block = hitBlock,
                    PlaceBlock = placeBlock,
                    Normal = lastNormal,
                    Distance = t
                };
            }

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
        // n is axis-aligned and equals (-1,0,0) etc.
        return new Vector3Int((int)n.X, (int)n.Y, (int)n.Z);
    }
}
