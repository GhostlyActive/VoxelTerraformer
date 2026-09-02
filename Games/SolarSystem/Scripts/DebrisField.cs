using Raylib_cs;
using System.Numerics;
using VoxelEngine.Effects;

namespace Terraformer.Games.SolarSystem;

/// <summary>
/// The rubble a hit throws off. Chunks keep flying long after the explosion has faded: they fall
/// under the same gravity as the ship, so they arc back onto the planet or drift away for good,
/// and a shot can hit them again. Without this the destruction would vanish with the particles.
/// </summary>
public sealed class DebrisField
{
    /// <summary>Hard cap; the oldest chunk gives way to a new one</summary>
    private const int MaxChunks = 1400;

    private const float Lifetime = 90f;

    private sealed class Chunk
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 SpinAxis;
        public float Spin;
        public float SpinSpeed;
        public float Size;
        public float Life;

        /// <summary>Seconds during which the chunk ignores solid ground it is still inside of</summary>
        public float Grace;

        public Color Color;
    }

    private readonly List<Chunk> _chunks = new();
    private readonly ParticleSystem _particles;

    private int _oldest;

    public int Count => _chunks.Count;

    public DebrisField(ParticleSystem particles)
    {
        _particles = particles;
    }

    public void Clear()
    {
        _chunks.Clear();
        _oldest = 0;
    }

    /// <summary>
    /// One chunk with a velocity the caller worked out. Used by the core detonation, where the
    /// rubble is the material the blast just removed and has to fly the way that material went.
    ///
    /// <paramref name="grace"/> keeps it from being swallowed by crust that the expanding shell has
    /// not reached yet — without it most of a planet's rubble is absorbed the instant it spawns.
    /// </summary>
    public void SpawnAt(Vector3 position, Vector3 velocity, Color color, float size, float grace = 0f)
    {
        Add(new Chunk
        {
            Position = position,
            Velocity = velocity,
            SpinAxis = RandomDirection(),
            SpinSpeed = 30f + Random.Shared.NextSingle() * 220f,
            Size = size * (0.6f + Random.Shared.NextSingle() * 0.9f),
            Life = Lifetime * (0.6f + Random.Shared.NextSingle() * 0.8f),
            Grace = grace,
            Color = color,
        });
    }

    /// <summary>
    /// Throw <paramref name="count"/> chunks out of an impact. They inherit the body's orbital
    /// motion, otherwise the rubble would visibly lag behind the planet it came from.
    /// </summary>
    public void Spawn(Vector3 origin, Vector3 inheritedVelocity, Color color, float size, float spread, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Vector3 direction = RandomDirection();

            var chunk = new Chunk
            {
                Position = origin + direction * spread * Random.Shared.NextSingle(),
                Velocity = inheritedVelocity + direction * (spread * (0.6f + Random.Shared.NextSingle() * 1.6f)),
                SpinAxis = RandomDirection(),
                SpinSpeed = 20f + Random.Shared.NextSingle() * 140f,
                Size = size * (0.5f + Random.Shared.NextSingle()),
                Life = Lifetime * (0.6f + Random.Shared.NextSingle() * 0.8f),
                Color = color,
            };

            Add(chunk);
        }
    }

    private void Add(Chunk chunk)
    {
        if (_chunks.Count < MaxChunks)
        {
            _chunks.Add(chunk);
            return;
        }

        _chunks[_oldest] = chunk;
        _oldest = (_oldest + 1) % MaxChunks;
    }

    /// <summary>
    /// <paramref name="gravityAt"/> is the same field the ship flies through, so rubble falls back
    /// onto the planet it was blasted off. A chunk that reaches solid ground per
    /// <paramref name="isInsideSolid"/> is absorbed with a small puff.
    /// </summary>
    public void Update(float dt, Func<Vector3, Vector3> gravityAt, Func<Vector3, bool> isInsideSolid)
    {
        for (int i = _chunks.Count - 1; i >= 0; i--)
        {
            Chunk chunk = _chunks[i];

            chunk.Life -= dt;
            chunk.Grace -= dt;
            chunk.Velocity += gravityAt(chunk.Position) * dt;
            chunk.Position += chunk.Velocity * dt;
            chunk.Spin += chunk.SpinSpeed * dt;

            if (chunk.Life > 0f && (chunk.Grace > 0f || !isInsideSolid(chunk.Position))) continue;

            // Landed or burnt out: leave a small puff rather than blinking out
            _particles.SpawnExplosion(chunk.Position, chunk.Color, chunk.Size * 0.05f);
            RemoveAt(i);
        }
    }

    /// <summary>Index of the chunk a shot at this point hits, or -1</summary>
    public int FindHit(Vector3 point, float shotRadius)
    {
        for (int i = 0; i < _chunks.Count; i++)
        {
            float reach = _chunks[i].Size * 0.5f + shotRadius;
            if (Vector3.DistanceSquared(_chunks[i].Position, point) <= reach * reach) return i;
        }

        return -1;
    }

    /// <summary>Blow a hit chunk apart and report where it was, for the impact effect</summary>
    public (Vector3 Position, Color Color, float Size) Shatter(int index)
    {
        Chunk chunk = _chunks[index];
        RemoveAt(index);

        return (chunk.Position, chunk.Color, chunk.Size);
    }

    private void RemoveAt(int index)
    {
        _chunks.RemoveAt(index);
        if (_oldest > index || _oldest >= _chunks.Count) _oldest = 0;
    }

    public void Draw()
    {
        foreach (Chunk chunk in _chunks)
        {
            Rlgl.PushMatrix();
            Rlgl.Translatef(chunk.Position.X, chunk.Position.Y, chunk.Position.Z);
            Rlgl.Rotatef(chunk.Spin, chunk.SpinAxis.X, chunk.SpinAxis.Y, chunk.SpinAxis.Z);

            Raylib.DrawCubeV(Vector3.Zero, new Vector3(chunk.Size), chunk.Color);

            // A second, smaller cube offset off-centre stops the rubble reading as uniform dice
            Raylib.DrawCubeV(
                new Vector3(chunk.Size * 0.35f, chunk.Size * 0.2f, -chunk.Size * 0.3f),
                new Vector3(chunk.Size * 0.55f),
                chunk.Color);

            Rlgl.PopMatrix();
        }
    }

    private static Vector3 RandomDirection()
    {
        while (true)
        {
            var candidate = new Vector3(
                Random.Shared.NextSingle() * 2f - 1f,
                Random.Shared.NextSingle() * 2f - 1f,
                Random.Shared.NextSingle() * 2f - 1f);

            float lengthSquared = candidate.LengthSquared();
            if (lengthSquared is > 0.0001f and <= 1f) return candidate / MathF.Sqrt(lengthSquared);
        }
    }
}
