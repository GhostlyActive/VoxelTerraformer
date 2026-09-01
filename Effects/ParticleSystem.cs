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

        /// <summary>Faktor auf die Schwerkraft: 1 = Trümmer, negativ = aufsteigender Rauch</summary>
        public float GravityScale;

        /// <summary>Wächst der Partikel über seine Lebenszeit? (Rauch quillt auf)</summary>
        public float Growth;
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
                GravityScale = 1f,
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
                GravityScale = 1f,
            });
        }
    }

    /// <summary>Aufsteigender Voxelrauch, z. B. hinter einem Raketentriebwerk</summary>
    public void SpawnSmoke(Vector3 origin, Vector3 drift, int count, float scale = 1f)
    {
        for (int i = 0; i < count; i++)
        {
            Vector3 direction = RandomDirection();
            byte grey = (byte)RandomRange(190f, 245f);

            Spawn(new Particle
            {
                Position = origin + direction * (0.35f * scale),
                Velocity = drift + direction * RandomRange(0.6f, 2.2f) * scale,
                MaxLife = RandomRange(1.1f, 2.4f),
                Size = RandomRange(0.22f, 0.5f) * scale,
                Color = new Color(grey, grey, (byte)Math.Min(255, grey + 6), (byte)255),
                GravityScale = RandomRange(-0.12f, -0.03f), // Rauch steigt statt zu fallen
                Growth = RandomRange(0.8f, 1.8f),
            });
        }
    }

    /// <summary>Explosion: heller Feuerkern, Rauchwolke und geworfene Trümmer in Geländefarbe</summary>
    public void SpawnExplosion(Vector3 center, Color debris, float power)
    {
        for (int i = 0; i < 40; i++)
        {
            Vector3 direction = RandomDirection();
            Spawn(new Particle
            {
                Position = center + direction * 0.5f,
                Velocity = direction * RandomRange(4f, 14f) * power + Vector3.UnitY * RandomRange(2f, 9f),
                MaxLife = RandomRange(0.9f, 2.2f),
                Size = RandomRange(0.12f, 0.34f),
                Color = Tint(debris),
                GravityScale = 1f,
                Growth = 0f,
            });
        }

        for (int i = 0; i < 26; i++)
        {
            Vector3 direction = RandomDirection();
            byte heat = (byte)RandomRange(200f, 255f);
            Spawn(new Particle
            {
                Position = center + direction * 0.4f,
                Velocity = direction * RandomRange(2f, 7f) + Vector3.UnitY * RandomRange(1f, 5f),
                MaxLife = RandomRange(0.35f, 0.8f),
                Size = RandomRange(0.3f, 0.7f),
                Color = new Color(heat, (byte)(heat * 0.55f), (byte)40, (byte)255),
                GravityScale = -0.2f,
                Growth = 2.2f,
            });
        }

        SpawnSmoke(center + Vector3.UnitY * 0.6f, Vector3.UnitY * 1.5f, 30, 1.6f);
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

            p.Velocity.Y -= Gravity * p.GravityScale * dt;
            p.Position += p.Velocity * dt;

            // Boden-Bounce gegen die Voxelwelt (bewusst nur nach unten geprüft)
            if (p.Velocity.Y < 0f && p.GravityScale > 0f)
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
            float age = 1f - p.Life / p.MaxLife;
            float size = p.Size * shrink * (1f + p.Growth * age);

            Raylib.DrawCubeV(p.Position, new Vector3(size), p.Color);
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
