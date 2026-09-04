using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Scenes;

namespace Games.RocketStorm;

/// <summary>What is coming in: each kind asks something different of the player</summary>
public enum RocketKind
{
    /// <summary>Arcs in from the horizon; a hole in the ground is cover</summary>
    Standard,

    /// <summary>Splits over the target into three small ones; cover has to be wide</summary>
    Cluster,

    /// <summary>Slow and heavy, twice the crater and twice the reach; get far away or shoot it</summary>
    Buster,

    /// <summary>Flies low and follows you; only the flak stops it</summary>
    Seeker,
}

/// <summary>
/// The attack: waves of rockets that arc in on the player from the horizon, each announced by a
/// ring on the ground that contracts until it arrives, plus seekers that fly low and turn after
/// the player. Every wave sends more and worse of them. The flak takes anything down mid-air.
/// </summary>
public sealed class RocketBarrage
{
    private const float FlightSeconds = 2.6f;
    private const float ApexHeight = 34f;
    private const float LaunchDistance = 120f;

    private const float SeekerSpeed = 11f;
    private const float SeekerTurnPerSecond = 1.4f;
    private const float SeekerAltitude = 2.2f;
    private const float SeekerFuse = 2.6f;

    private readonly RocketStormSettings _settings;

    private sealed class Incoming
    {
        public required Rocket Rocket { get; init; }
        public required RocketKind Kind { get; init; }
        public required Vector3 Impact { get; init; }
        public required float Flight { get; init; }
        public float TimeLeft;
        public bool Split;
    }

    private sealed class Seeker
    {
        public Vector3 Position;
        public Vector3 Heading;
        public float Life = 40f;
    }

    /// <summary>A rocket or seeker was destroyed in the air: where, and what kind</summary>
    public event Action<Vector3, RocketKind>? ShotDown;

    /// <summary>Something reached the player or the ground: where, with what crater radius and damage scale</summary>
    public event Action<Vector3, float, float>? Impact;

    private readonly VoxelTerrainScene _scene;
    private readonly AudioBank _audio;
    private readonly List<Incoming> _live = new();
    private readonly List<Seeker> _seekers = new();

    private int _remainingInWave;
    private float _nextLaunch;
    private float _waveBreak = 2.5f;

    public int Wave { get; private set; }

    public int InFlight => _live.Count + _seekers.Count;

    /// <summary>Seconds until the next wave; 0 while one is running</summary>
    public float WaveBreak => _waveBreak;

    /// <summary>Crater radius of a standard rocket, and with it the zone where a hit lands at full force</summary>
    public float CraterRadius => _settings.CraterRadius;

    public RocketBarrage(VoxelTerrainScene scene, AudioBank audio, RocketStormSettings settings)
    {
        _scene = scene;
        _audio = audio;
        _settings = settings;
    }

    public void Reset()
    {
        _live.Clear();
        _seekers.Clear();
        Wave = 0;
        _remainingInWave = 0;
        _waveBreak = 2.5f;
    }

    public void Update(float dt, Vector3 target)
    {
        UpdateSchedule(dt, target);
        UpdateRockets(dt);
        UpdateSeekers(dt, target);
    }

    private void UpdateRockets(float dt)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            Incoming incoming = _live[i];
            incoming.TimeLeft = MathF.Max(0f, incoming.TimeLeft - dt);
            incoming.Rocket.Update(dt, _scene.Particles);

            // A cluster rocket breaks up over the target: three small ones rain down around it
            if (incoming.Kind == RocketKind.Cluster && !incoming.Split && incoming.TimeLeft < incoming.Flight * 0.45f)
            {
                incoming.Split = true;
                _live.RemoveAt(i);
                Scatter(incoming);
                continue;
            }

            if (!incoming.Rocket.Landed) continue;

            _live.RemoveAt(i);
            Impact?.Invoke(incoming.Impact, CraterOf(incoming.Kind), DamageOf(incoming.Kind));
        }
    }

    private void Scatter(Incoming parent)
    {
        for (int i = 0; i < 3; i++)
        {
            float angle = i / 3f * MathF.Tau + Random.Shared.NextSingle();
            Vector3 impact = OnGround(parent.Impact + new Vector3(MathF.Cos(angle) * 5f, 0f, MathF.Sin(angle) * 5f));
            Vector3 start = parent.Rocket.Position;
            var apex = new Vector3((start.X + impact.X) * 0.5f, MathF.Max(start.Y, impact.Y) + 6f, (start.Z + impact.Z) * 0.5f);

            _live.Add(new Incoming
            {
                Rocket = new Rocket(start, apex, impact, 1.1f) { Scale = 0.55f, Accent = new Color(255, 170, 60, 255) },
                Kind = RocketKind.Standard,
                Impact = impact,
                Flight = 1.1f,
                TimeLeft = 1.1f,
            });
        }

        _audio.Play("launch", 0.3f, 1.4f);
    }

    private void UpdateSeekers(float dt, Vector3 target)
    {
        for (int i = _seekers.Count - 1; i >= 0; i--)
        {
            Seeker seeker = _seekers[i];
            seeker.Life -= dt;

            // Turn towards the player at a limited rate, so it can be outrun for a moment
            Vector3 toTarget = target + Vector3.UnitY - seeker.Position;
            float distance = toTarget.Length();
            Vector3 wanted = distance > 1e-3f ? toTarget / distance : seeker.Heading;
            seeker.Heading = Vector3.Normalize(Vector3.Lerp(seeker.Heading, wanted, MathF.Min(1f, SeekerTurnPerSecond * dt)));

            seeker.Position += seeker.Heading * SeekerSpeed * dt;

            // Hug the ground: never below the surface, never far above it
            float floor = _scene.SurfaceHeight(seeker.Position.X, seeker.Position.Z) + SeekerAltitude;
            seeker.Position.Y = MathF.Max(seeker.Position.Y, floor);
            seeker.Position.Y += (floor - seeker.Position.Y) * MathF.Min(1f, 2f * dt);

            _scene.Particles.SpawnTrail(seeker.Position, -seeker.Heading * 2f, new Color(255, 80, 60, 255), 0.25f);

            if (distance < SeekerFuse || seeker.Life <= 0f)
            {
                _seekers.RemoveAt(i);
                Impact?.Invoke(seeker.Position, CraterOf(RocketKind.Seeker), DamageOf(RocketKind.Seeker));
            }
        }
    }

    private void UpdateSchedule(float dt, Vector3 target)
    {
        if (_remainingInWave <= 0)
        {
            // The gap between waves is the time you get to dig in
            if (InFlight > 0) return;

            _waveBreak -= dt;
            if (_waveBreak > 0f) return;

            Wave++;
            _remainingInWave = _settings.RocketsInFirstWave + Wave - 1;
            _nextLaunch = 0f;
            _waveBreak = 0f;
            return;
        }

        _nextLaunch -= dt;
        if (_nextLaunch > 0f) return;

        Launch(target, PickKind());
        _remainingInWave--;

        _nextLaunch = MathF.Max(0.35f, 1.5f - Wave * 0.08f);
        if (_remainingInWave == 0) _waveBreak = _settings.WavePause;
    }

    /// <summary>The mix hardens with the waves: clusters from the second, seekers from the third, busters from the fourth</summary>
    private RocketKind PickKind()
    {
        float roll = Random.Shared.NextSingle();

        if (Wave >= 4 && roll < 0.15f) return RocketKind.Buster;
        if (Wave >= 3 && roll < 0.40f) return RocketKind.Seeker;
        if (Wave >= 2 && roll < 0.65f) return RocketKind.Cluster;

        return RocketKind.Standard;
    }

    private void Launch(Vector3 target, RocketKind kind)
    {
        // Spread the impacts: some land right on the player, others are warning shots
        float angle = Random.Shared.NextSingle() * MathF.Tau;
        float spread = 2f + Random.Shared.NextSingle() * (6f + Wave);
        var offset = new Vector3(MathF.Cos(angle) * spread, 0f, MathF.Sin(angle) * spread);

        Vector3 impact = OnGround(target + offset);

        float fromAngle = Random.Shared.NextSingle() * MathF.Tau;
        Vector3 pad = OnGround(impact + new Vector3(
            MathF.Cos(fromAngle) * LaunchDistance, 0f, MathF.Sin(fromAngle) * LaunchDistance));

        if (kind == RocketKind.Seeker)
        {
            _seekers.Add(new Seeker
            {
                Position = pad + Vector3.UnitY * SeekerAltitude,
                Heading = Vector3.Normalize(new Vector3(impact.X - pad.X, 0f, impact.Z - pad.Z)),
            });
            _audio.Play("launch", 0.3f, 1.6f);
            return;
        }

        float flight = kind == RocketKind.Buster ? FlightSeconds * 1.7f : FlightSeconds;
        var apex = new Vector3(
            (pad.X + impact.X) * 0.5f,
            MathF.Max(pad.Y, impact.Y) + ApexHeight * (kind == RocketKind.Buster ? 1.5f : 1f),
            (pad.Z + impact.Z) * 0.5f);

        var rocket = new Rocket(pad + Vector3.UnitY * 1.2f, apex, impact, flight)
        {
            Scale = kind switch { RocketKind.Buster => 2.2f, RocketKind.Cluster => 1.3f, _ => 1f },
            Accent = kind switch
            {
                RocketKind.Buster => new Color(90, 90, 100, 255),
                RocketKind.Cluster => new Color(255, 170, 60, 255),
                _ => new Color(214, 78, 62, 255),
            },
        };

        _live.Add(new Incoming { Rocket = rocket, Kind = kind, Impact = impact, Flight = flight, TimeLeft = flight });

        _audio.Play("launch", 0.35f, kind == RocketKind.Buster ? 0.6f : 0.9f + Random.Shared.NextSingle() * 0.2f);
    }

    private float CraterOf(RocketKind kind) => kind switch
    {
        RocketKind.Buster => _settings.CraterRadius * 2f,
        RocketKind.Seeker => _settings.CraterRadius * 0.7f,
        _ => _settings.CraterRadius,
    };

    private static float DamageOf(RocketKind kind) => kind switch
    {
        RocketKind.Buster => 2f,
        RocketKind.Seeker => 0.8f,
        _ => 1f,
    };

    private Vector3 OnGround(Vector3 position)
        => new(position.X, _scene.SurfaceHeight(position.X, position.Z), position.Z);

    /// <summary>
    /// The flak: whatever flies closest to the aim line within its reach goes up. Returns where
    /// it went up, or null for a miss.
    /// </summary>
    public Vector3? ShootDown(Ray aim, float reach)
    {
        Vector3 direction = Vector3.Normalize(aim.Direction);
        float best = float.MaxValue;
        int bestRocket = -1, bestSeeker = -1;

        for (int i = 0; i < _live.Count; i++)
        {
            float miss = MissDistance(aim.Position, direction, _live[i].Rocket.Position, reach);
            float slack = 2f + _live[i].Rocket.Scale;
            if (miss < slack && miss < best) { best = miss; bestRocket = i; bestSeeker = -1; }
        }

        for (int i = 0; i < _seekers.Count; i++)
        {
            float miss = MissDistance(aim.Position, direction, _seekers[i].Position, reach);
            if (miss < 2f && miss < best) { best = miss; bestSeeker = i; bestRocket = -1; }
        }

        if (bestRocket >= 0)
        {
            Incoming hit = _live[bestRocket];
            _live.RemoveAt(bestRocket);
            ShotDown?.Invoke(hit.Rocket.Position, hit.Kind);
            return hit.Rocket.Position;
        }

        if (bestSeeker >= 0)
        {
            Seeker hit = _seekers[bestSeeker];
            _seekers.RemoveAt(bestSeeker);
            ShotDown?.Invoke(hit.Position, RocketKind.Seeker);
            return hit.Position;
        }

        return null;
    }

    /// <summary>Distance of a point from the aim line, or infinity when it lies behind the muzzle or past the reach</summary>
    private static float MissDistance(Vector3 origin, Vector3 direction, Vector3 point, float reach)
    {
        Vector3 relative = point - origin;
        float along = Vector3.Dot(relative, direction);
        if (along < 0f || along > reach) return float.MaxValue;

        return (relative - direction * along).Length();
    }

    public void Draw3D()
    {
        foreach (Incoming incoming in _live)
        {
            incoming.Rocket.Draw();
            DrawMarker(incoming);
        }

        foreach (Seeker seeker in _seekers)
        {
            Raylib.DrawCubeV(seeker.Position, new Vector3(0.9f, 0.5f, 0.9f), new Color(40, 40, 50, 255));
            Raylib.DrawCubeV(seeker.Position + seeker.Heading * 0.5f, new Vector3(0.35f), new Color(255, 70, 50, 255));
        }
    }

    /// <summary>Target ring: contracts onto the impact point and blinks faster towards the end</summary>
    private void DrawMarker(Incoming incoming)
    {
        float progress = 1f - incoming.TimeLeft / incoming.Flight;
        float crater = CraterOf(incoming.Kind);
        float radius = crater * (1.9f - progress);

        float blink = MathF.Sin((float)Raylib.GetTime() * (6f + progress * 18f));
        byte alpha = (byte)(120 + 100 * (blink * 0.5f + 0.5f));

        Vector3 center = incoming.Impact + Vector3.UnitY * 0.15f;
        var color = new Color((byte)255, (byte)(90 + 60 * (1f - progress)), (byte)70, alpha);

        Raylib.DrawCircle3D(center, radius, Vector3.UnitX, 90f, color);
        Raylib.DrawCircle3D(center, crater, Vector3.UnitX, 90f, new Color((byte)255, (byte)70, (byte)60, (byte)70));
    }
}
