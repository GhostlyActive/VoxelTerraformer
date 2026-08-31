using Raylib_cs;
using System.Numerics;
using Terraformer.World;

namespace Terraformer.Effects;

/// <summary>
/// Kleine Würfel-Partikel für Abbau-/Platzier-Effekte. Bewusst simpel:
/// Schwerkraft, Boden-Bounce gegen die Voxelwelt, Schrumpfen am Lebensende.
/// </summary>
public sealed class ParticleSystem
{
    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float MaxLife;
        public float Size;
        public Color Color;
    }

    private const int MaxParticles = 2048;
    private const float Gravity = 22f;

    private readonly Particle[] _particles = new Particle[MaxParticles];
    private int _count;

    /// <summary>Aktuelles Weltlicht (0..~1) — wird beim Spawn in die Farbe eingebacken,
    /// weil die Partikel über den unbeleuchteten Default-Shader laufen</summary>
    public float LightScale { get; set; } = 1f;

    public int ActiveParticles => _count;

    public void SpawnBlockBreak(Vector3 blockCenter, Color albedo)
    {
        for (int i = 0; i < 24; i++)
        {
            Vector3 direction = RandomDirection();
            Spawn(new Particle
            {
                Position = blockCenter + direction * 0.3f,
                Velocity = direction * RandomRange(1.5f, 4.5f) + Vector3.UnitY * RandomRange(1f, 4f),
                MaxLife = RandomRange(0.7f, 1.4f),
                Size = RandomRange(0.07f, 0.16f),
                Color = Tint(albedo),
            });
        }
    }

    public void SpawnBlockPlace(Vector3 blockCenter, Color albedo)
    {
        for (int i = 0; i < 10; i++)
        {
            // flacher Ring nach außen — dezentes "Pop" beim Setzen
            float angle = RandomRange(0f, MathF.Tau);
            var direction = new Vector3(MathF.Cos(angle), 0.25f, MathF.Sin(angle));
            Spawn(new Particle
            {
                Position = blockCenter + direction * 0.55f,
                Velocity = direction * RandomRange(1.5f, 3f),
                MaxLife = RandomRange(0.25f, 0.45f),
                Size = RandomRange(0.05f, 0.09f),
                Color = Tint(albedo),
            });
        }
    }

    public void Update(VoxelWorld world, float dt)
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            ref Particle p = ref _particles[i];

            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _particles[i] = _particles[--_count];
                continue;
            }

            p.Velocity.Y -= Gravity * dt;
            p.Position += p.Velocity * dt;

            // Boden-Bounce gegen die Voxelwelt (bewusst nur nach unten geprüft)
            if (p.Velocity.Y < 0f)
            {
                float half = p.Size * 0.5f;
                int bx = (int)MathF.Floor(p.Position.X);
                int by = (int)MathF.Floor(p.Position.Y - half);
                int bz = (int)MathF.Floor(p.Position.Z);

                if (BlockRegistry.IsSolid(world.GetBlock(bx, by, bz)))
                {
                    p.Position.Y = by + 1 + half + 0.001f;
                    p.Velocity.Y *= -0.35f;
                    p.Velocity.X *= 0.7f;
                    p.Velocity.Z *= 0.7f;
                }
            }
        }
    }

    public void Draw()
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly Particle p = ref _particles[i];

            // im letzten Viertel der Lebenszeit schrumpfen statt hart verschwinden
            float shrink = Math.Min(1f, p.Life / (0.25f * p.MaxLife));
            Raylib.DrawCubeV(p.Position, new Vector3(p.Size * shrink), p.Color);
        }
    }

    private void Spawn(Particle particle)
    {
        if (_count >= MaxParticles) return;
        particle.Life = particle.MaxLife;
        _particles[_count++] = particle;
    }

    private Color Tint(Color albedo)
    {
        float factor = LightScale * RandomRange(0.85f, 1.05f);
        return new Color(
            (byte)Math.Clamp(albedo.R * factor, 0f, 255f),
            (byte)Math.Clamp(albedo.G * factor, 0f, 255f),
            (byte)Math.Clamp(albedo.B * factor, 0f, 255f),
            (byte)255);
    }

    private static Vector3 RandomDirection()
    {
        // Rejection Sampling: gleichverteilte Richtung ohne Häufung an den Polen
        while (true)
        {
            var v = new Vector3(
                RandomRange(-1f, 1f),
                RandomRange(-1f, 1f),
                RandomRange(-1f, 1f));

            float lengthSquared = v.LengthSquared();
            if (lengthSquared > 0.001f && lengthSquared <= 1f)
                return v / MathF.Sqrt(lengthSquared);
        }
    }

    private static float RandomRange(float min, float max)
        => min + Random.Shared.NextSingle() * (max - min);
}
