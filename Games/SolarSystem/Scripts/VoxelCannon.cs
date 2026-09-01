using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Effects;

namespace Terraformer.Games.SolarSystem;

/// <summary>
/// Die Bordkanone: verschießt Voxelbälle, die geradeaus weiterfliegen, bis sie einen
/// Himmelskörper treffen oder ihre Zeit abgelaufen ist. Getroffen wird in Teilschritten
/// entlang der Flugstrecke — bei über 200 m/s läge zwischen zwei Bildern sonst ein ganzer
/// Planet, durch den der Schuss einfach hindurchspringt.
/// </summary>
public sealed class VoxelCannon
{
    private const float ShotSpeed = 260f;
    private const float ShotLifetime = 12f;
    private const float ShotSize = 1.6f;
    private const float MaxStepPerSubstep = 1.5f;

    /// <summary>Radius der Sprengung im Ziel, in Metern</summary>
    public const float BlastRadius = 5.5f;

    private sealed class Shot
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
    }

    private readonly ParticleSystem _particles;
    private readonly AudioBank _audio;
    private readonly List<Shot> _shots = new();

    private float _cooldown;

    /// <summary>Zeit zwischen zwei Schüssen in Sekunden</summary>
    public float FireInterval { get; init; } = 0.22f;

    public int ActiveShots => _shots.Count;

    public VoxelCannon(ParticleSystem particles, AudioBank audio)
    {
        _particles = particles;
        _audio = audio;
    }

    public void Fire(Vector3 origin, Vector3 direction, Vector3 shipVelocity)
    {
        if (_cooldown > 0f) return;

        _cooldown = FireInterval;
        _shots.Add(new Shot
        {
            // Vor dem Cockpit starten, sonst steckt der Ball im eigenen Bild
            Position = origin + direction * 3f,
            Velocity = direction * ShotSpeed + shipVelocity,
            Life = ShotLifetime,
        });

        _audio.Play("shot", 0.5f, 0.9f + Random.Shared.NextSingle() * 0.25f);
    }

    public void Clear() => _shots.Clear();

    public void Update(float dt, IReadOnlyList<CelestialBody> bodies, Action<CelestialBody, Vector3> onHit)
    {
        _cooldown = MathF.Max(0f, _cooldown - dt);

        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            Shot shot = _shots[i];
            shot.Life -= dt;

            if (shot.Life <= 0f)
            {
                _shots.RemoveAt(i);
                continue;
            }

            if (Advance(shot, dt, bodies, onHit)) _shots.RemoveAt(i);
        }
    }

    /// <summary>Bewegt einen Schuss um ein Bild weiter; true, wenn er dabei eingeschlagen ist</summary>
    private bool Advance(Shot shot, float dt, IReadOnlyList<CelestialBody> bodies, Action<CelestialBody, Vector3> onHit)
    {
        float distance = shot.Velocity.Length() * dt;
        int substeps = Math.Clamp((int)MathF.Ceiling(distance / MaxStepPerSubstep), 1, 32);
        float step = dt / substeps;

        for (int i = 0; i < substeps; i++)
        {
            shot.Position += shot.Velocity * step;

            CelestialBody? hit = FindHit(shot.Position, bodies);
            if (hit == null) continue;

            onHit(hit, shot.Position);
            return true;
        }

        return false;
    }

    private static CelestialBody? FindHit(Vector3 point, IReadOnlyList<CelestialBody> bodies)
    {
        foreach (CelestialBody body in bodies)
        {
            // Grobtest an der Hüllkugel, bevor die Weltkoordinate ins Voxelgitter umgerechnet wird
            float reach = body.Body.BoundingRadius;
            if (Vector3.DistanceSquared(point, body.Body.Position) > reach * reach) continue;

            if (body.Body.IsSolidAt(point)) return body;
        }

        return null;
    }

    /// <summary>Trümmer und Feuer am Einschlag; die Farbe kommt vom getroffenen Material</summary>
    public void SpawnImpact(Vector3 point, Color debris)
    {
        _particles.SpawnExplosion(point, debris, 1.1f);
        _audio.Play("impact", 0.75f, 0.85f + Random.Shared.NextSingle() * 0.3f);
    }

    public void Draw()
    {
        foreach (Shot shot in _shots)
        {
            Raylib.DrawCubeV(shot.Position, new Vector3(ShotSize), new Color(255, 236, 140, 255));

            // Kurzer Schweif nach hinten, damit die Flugbahn im leeren Raum ablesbar bleibt
            Vector3 tail = shot.Position - Vector3.Normalize(shot.Velocity) * (ShotSize * 2.2f);
            Raylib.DrawCubeV(tail, new Vector3(ShotSize * 0.55f), new Color(255, 150, 70, 200));
        }
    }
}
