using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Effects;

namespace Terraformer.Games.SolarSystem;

/// <summary>
/// The ship's cannon. Holding the trigger packs more material into the round: a tap sends a
/// small fast pellet, a full charge lobs a slow boulder that takes a bite out of a moon.
///
/// Rounds fly straight until they hit a body or run out of time. Hits are checked in substeps
/// along the flight path — at 300 m/s a whole planet fits between two frames, and the round
/// would sail straight through it.
/// </summary>
public sealed class VoxelCannon
{
    /// <summary>Hold time for a full charge, in seconds</summary>
    public const float FullChargeSeconds = 1.5f;

    private const float MinShotSize = 1.3f;
    private const float MaxShotSize = 4.6f;
    private const float MinBlastRadius = 3.5f;
    private const float MaxBlastRadius = 13f;

    // Heavy rounds fly slower: the arc gives the size some weight
    private const float LightShotSpeed = 320f;
    private const float HeavyShotSpeed = 190f;

    private const float ShotLifetime = 14f;
    private const float ReloadSeconds = 0.18f;
    private const float MaxStepPerSubstep = 1.5f;
    private const float TrailInterval = 0.02f;

    private static readonly Vector3[] _faceOffsets =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    private sealed class Shot
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float Size;
        public float BlastRadius;
        public float Spin;
        public float SpinSpeed;
        public float TrailCooldown;
    }

    private readonly ParticleSystem _particles;
    private readonly AudioBank _audio;
    private readonly List<Shot> _shots = new();

    private float _charge;
    private float _reload;

    public int ActiveShots => _shots.Count;

    /// <summary>Is the trigger held and the round growing?</summary>
    public bool Charging { get; private set; }

    /// <summary>Charge level 0..1, for the display</summary>
    public float Charge01 => Math.Clamp(_charge / FullChargeSeconds, 0f, 1f);

    public VoxelCannon(ParticleSystem particles, AudioBank audio)
    {
        _particles = particles;
        _audio = audio;
    }

    /// <summary>Call while the trigger is down</summary>
    public void Hold(float dt)
    {
        if (_reload > 0f) return;

        Charging = true;
        _charge = MathF.Min(_charge + dt, FullChargeSeconds);
    }

    /// <summary>Call when the trigger comes up: fires whatever has been packed in so far</summary>
    public void Release(Vector3 origin, Vector3 direction, Vector3 shipVelocity)
    {
        if (!Charging) return;

        float charge = Charge01;
        float size = SizeFor(charge);
        float speed = Lerp(LightShotSpeed, HeavyShotSpeed, charge);

        _shots.Add(new Shot
        {
            // Start ahead of the cockpit, otherwise the round sits in your own view
            Position = origin + direction * (2f + size),
            Velocity = direction * speed + shipVelocity,
            Life = ShotLifetime,
            Size = size,
            BlastRadius = Lerp(MinBlastRadius, MaxBlastRadius, charge),
            SpinSpeed = 90f + Random.Shared.NextSingle() * 220f,
        });

        // Bigger round, deeper report
        _audio.Play("shot", 0.4f + charge * 0.35f, 1.15f - charge * 0.5f);

        Charging = false;
        _charge = 0f;
        _reload = ReloadSeconds;
    }

    /// <summary>Diameter a round would have at this charge level</summary>
    public static float SizeFor(float charge01) => Lerp(MinShotSize, MaxShotSize, Math.Clamp(charge01, 0f, 1f));

    public void Clear()
    {
        _shots.Clear();
        Charging = false;
        _charge = 0f;
    }

    public void Update(float dt, IReadOnlyList<CelestialBody> bodies, Action<CelestialBody, Vector3, float> onHit)
    {
        _reload = MathF.Max(0f, _reload - dt);

        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            Shot shot = _shots[i];
            shot.Life -= dt;
            shot.Spin += shot.SpinSpeed * dt;

            if (shot.Life <= 0f)
            {
                _shots.RemoveAt(i);
                continue;
            }

            LeaveTrail(shot, dt);

            if (Advance(shot, dt, bodies, onHit)) _shots.RemoveAt(i);
        }
    }

    private void LeaveTrail(Shot shot, float dt)
    {
        shot.TrailCooldown -= dt;
        if (shot.TrailCooldown > 0f) return;

        shot.TrailCooldown = TrailInterval;

        Vector3 backwards = -Vector3.Normalize(shot.Velocity);
        _particles.SpawnTrail(
            shot.Position + backwards * shot.Size,
            backwards * 6f,
            new Color(255, 170, 70, 255),
            shot.Size * 0.45f);
    }

    /// <summary>Moves a round one frame along its path; true if it hit something on the way</summary>
    private static bool Advance(Shot shot, float dt, IReadOnlyList<CelestialBody> bodies,
        Action<CelestialBody, Vector3, float> onHit)
    {
        float distance = shot.Velocity.Length() * dt;
        int substeps = Math.Clamp((int)MathF.Ceiling(distance / MaxStepPerSubstep), 1, 32);
        float step = dt / substeps;

        for (int i = 0; i < substeps; i++)
        {
            shot.Position += shot.Velocity * step;

            CelestialBody? hit = FindHit(shot.Position, bodies);
            if (hit == null) continue;

            onHit(hit, shot.Position, shot.BlastRadius);
            return true;
        }

        return false;
    }

    private static CelestialBody? FindHit(Vector3 point, IReadOnlyList<CelestialBody> bodies)
    {
        foreach (CelestialBody body in bodies)
        {
            // Cheap bounding-sphere test before converting the point into the voxel grid
            float reach = body.Body.BoundingRadius;
            if (Vector3.DistanceSquared(point, body.Body.Position) > reach * reach) continue;

            if (body.Body.IsSolidAt(point)) return body;
        }

        return null;
    }

    /// <summary>Debris and fire at the point of impact; the colour comes from the material that was hit</summary>
    public void SpawnImpact(Vector3 point, Color debris, float blastRadius)
    {
        float power = 0.7f + blastRadius / MaxBlastRadius;

        _particles.SpawnExplosion(point, debris, power);
        _audio.Play("impact", 0.6f + power * 0.25f, 1.2f - power * 0.35f);
    }

    /// <summary>
    /// The round growing at the muzzle while the trigger is held. Drawn far smaller than what
    /// actually leaves the barrel: at arm's length a full-size boulder blacks out the screen.
    /// </summary>
    public void DrawCharge(Vector3 position)
    {
        if (!Charging) return;

        const float previewScale = 0.2f;

        float charge = Charge01;
        float pulse = 1f + 0.08f * MathF.Sin((float)Raylib.GetTime() * 20f);

        DrawBall(position, SizeFor(charge) * previewScale * pulse, (float)Raylib.GetTime() * 140f, 0.5f + charge * 0.5f);
    }

    public void Draw()
    {
        foreach (Shot shot in _shots)
            DrawBall(shot.Position, shot.Size, shot.Spin, 1f);
    }

    /// <summary>
    /// A round as a small cluster of cubes: a bright core with six smaller blocks stuck to its
    /// faces, tumbling as it flies, wrapped in an additive glow. Reads as a lump of glowing
    /// voxels rather than the single cube it used to be.
    /// </summary>
    private static void DrawBall(Vector3 position, float size, float spin, float glow)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(position.X, position.Y, position.Z);
        Rlgl.Rotatef(spin, 0.42f, 1f, 0.28f);

        var core = new Color(255, 246, 205, 255);
        var shell = new Color(255, 162, 60, 255);

        Raylib.DrawCubeV(Vector3.Zero, new Vector3(size), core);

        foreach (Vector3 offset in _faceOffsets)
            Raylib.DrawCubeV(offset * (size * 0.52f), new Vector3(size * 0.6f), shell);

        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawCubeV(Vector3.Zero, new Vector3(size * 1.9f), new Color((byte)255, (byte)110, (byte)40, (byte)(46 * glow)));
        Raylib.EndBlendMode();

        Rlgl.PopMatrix();
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
