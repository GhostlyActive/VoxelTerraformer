using Raylib_cs;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Scenes;

namespace Games.RocketStorm;

/// <summary>
/// The attack: waves of rockets that arc in on the player from the horizon. Each one announces
/// its impact with a ring on the ground that contracts until the rocket arrives. Every wave sends
/// more of them, with shorter gaps in between.
/// </summary>
public sealed class RocketBarrage
{
    private const float FlightSeconds = 2.6f;
    private const float ApexHeight = 34f;
    private const float LaunchDistance = 120f;
    private const float WavePause = 4.5f;

    /// <summary>Crater radius, and with it the zone where a hit lands at full force</summary>
    public const float CraterRadius = 4.5f;

    private sealed class Incoming
    {
        public required Rocket Rocket { get; init; }
        public required Vector3 Impact { get; init; }
        public float TimeLeft;
    }

    private readonly VoxelTerrainScene _scene;
    private readonly AudioBank _audio;
    private readonly List<Incoming> _live = new();

    private int _remainingInWave;
    private float _nextLaunch;
    private float _waveBreak = 2.5f;

    public int Wave { get; private set; }

    public int InFlight => _live.Count;

    /// <summary>Seconds until the next wave; 0 while one is running</summary>
    public float WaveBreak => _waveBreak;

    public RocketBarrage(VoxelTerrainScene scene, AudioBank audio)
    {
        _scene = scene;
        _audio = audio;
    }

    public void Reset()
    {
        _live.Clear();
        Wave = 0;
        _remainingInWave = 0;
        _waveBreak = 2.5f;
    }

    public void Update(float dt, Vector3 target, Action<Vector3> onImpact)
    {
        UpdateSchedule(dt, target);

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            Incoming incoming = _live[i];
            incoming.TimeLeft = MathF.Max(0f, incoming.TimeLeft - dt);
            incoming.Rocket.Update(dt, _scene.Particles);

            if (!incoming.Rocket.Landed) continue;

            _live.RemoveAt(i);
            onImpact(incoming.Impact);
        }
    }

    private void UpdateSchedule(float dt, Vector3 target)
    {
        if (_remainingInWave <= 0)
        {
            // The gap between waves is the time you get to dig in
            if (_live.Count > 0) return;

            _waveBreak -= dt;
            if (_waveBreak > 0f) return;

            Wave++;
            _remainingInWave = 2 + Wave;
            _nextLaunch = 0f;
            _waveBreak = 0f;
            return;
        }

        _nextLaunch -= dt;
        if (_nextLaunch > 0f) return;

        Launch(target);
        _remainingInWave--;

        _nextLaunch = MathF.Max(0.35f, 1.5f - Wave * 0.08f);
        if (_remainingInWave == 0) _waveBreak = WavePause;
    }

    private void Launch(Vector3 target)
    {
        // Spread the impacts: some land right on the player, others are warning shots
        float angle = Random.Shared.NextSingle() * MathF.Tau;
        float spread = 2f + Random.Shared.NextSingle() * (6f + Wave);
        var offset = new Vector3(MathF.Cos(angle) * spread, 0f, MathF.Sin(angle) * spread);

        Vector3 impact = OnGround(target + offset);

        float fromAngle = Random.Shared.NextSingle() * MathF.Tau;
        Vector3 pad = OnGround(impact + new Vector3(
            MathF.Cos(fromAngle) * LaunchDistance, 0f, MathF.Sin(fromAngle) * LaunchDistance));

        var apex = new Vector3(
            (pad.X + impact.X) * 0.5f,
            MathF.Max(pad.Y, impact.Y) + ApexHeight,
            (pad.Z + impact.Z) * 0.5f);

        _live.Add(new Incoming
        {
            Rocket = new Rocket(pad + Vector3.UnitY * 1.2f, apex, impact, FlightSeconds),
            Impact = impact,
            TimeLeft = FlightSeconds,
        });

        _audio.Play("launch", 0.35f, 0.9f + Random.Shared.NextSingle() * 0.2f);
    }

    private Vector3 OnGround(Vector3 position)
        => new(position.X, _scene.SurfaceHeight(position.X, position.Z), position.Z);

    public void Draw3D()
    {
        foreach (Incoming incoming in _live)
        {
            incoming.Rocket.Draw();
            DrawMarker(incoming);
        }
    }

    /// <summary>Target ring: contracts onto the impact point and blinks faster towards the end</summary>
    private static void DrawMarker(Incoming incoming)
    {
        float progress = 1f - incoming.TimeLeft / FlightSeconds;
        float radius = CraterRadius * (1.9f - progress);

        float blink = MathF.Sin((float)Raylib.GetTime() * (6f + progress * 18f));
        byte alpha = (byte)(120 + 100 * (blink * 0.5f + 0.5f));

        Vector3 center = incoming.Impact + Vector3.UnitY * 0.15f;
        var color = new Color((byte)255, (byte)(90 + 60 * (1f - progress)), (byte)70, alpha);

        Raylib.DrawCircle3D(center, radius, Vector3.UnitX, 90f, color);
        Raylib.DrawCircle3D(center, CraterRadius, Vector3.UnitX, 90f, new Color((byte)255, (byte)70, (byte)60, (byte)70));
    }
}
