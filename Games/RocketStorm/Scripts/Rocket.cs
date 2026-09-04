using Raylib_cs;
using System.Numerics;
using VoxelEngine.Effects;

namespace Games.RocketStorm;

/// <summary>
/// A projectile on a scripted path (quadratic Bézier): launch, apex and impact are fixed the
/// moment it fires, which makes the point of impact predictable. The body is built from cubes,
/// tips into the direction of travel and trails smoke behind it.
/// </summary>
public sealed class Rocket
{
    private readonly Vector3 _start;
    private readonly Vector3 _control;
    private readonly Vector3 _target;
    private readonly float _flightSeconds;

    private float _elapsed;

    public Vector3 Position { get; private set; }
    public Vector3 Direction { get; private set; } = Vector3.UnitY;

    /// <summary>Impact reached; the caller then triggers the explosion</summary>
    public bool Landed => _elapsed >= _flightSeconds;

    /// <summary>Colour of the nose and fins; tells rocket types apart on screen</summary>
    public Color Accent { get; init; } = new(214, 78, 62, 255);

    /// <summary>Length of the body in metres; everything else scales with it</summary>
    public float Scale { get; init; } = 1f;

    public Rocket(Vector3 start, Vector3 apex, Vector3 target, float flightSeconds)
    {
        _start = start;
        _target = target;
        _flightSeconds = flightSeconds;

        // Control point chosen so the curve actually passes through the requested apex
        _control = 2f * apex - 0.5f * start - 0.5f * target;

        Position = start;
    }

    public void Update(float dt, ParticleSystem particles)
    {
        if (Landed) return;

        Vector3 previous = Position;

        _elapsed = MathF.Min(_elapsed + dt, _flightSeconds);
        float t = _elapsed / _flightSeconds;

        float inverse = 1f - t;
        Position = inverse * inverse * _start + 2f * inverse * t * _control + t * t * _target;

        Vector3 delta = Position - previous;
        if (delta.LengthSquared() > 1e-6f) Direction = Vector3.Normalize(delta);

        // Smoke sits at the tail and is pushed back against the direction of travel
        Vector3 nozzle = Position - Direction * (0.9f * Scale);
        particles.SpawnSmoke(nozzle, -Direction * 3.5f, 3, Scale);
    }

    public void Draw()
    {
        if (Landed) return;

        // Rotate into the direction of travel; the body's own axis is +Y
        Vector3 axis = Vector3.Cross(Vector3.UnitY, Direction);
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, Direction), -1f, 1f)) * (180f / MathF.PI);
        if (axis.LengthSquared() < 1e-6f) axis = Vector3.UnitX;

        Rlgl.PushMatrix();
        Rlgl.Translatef(Position.X, Position.Y, Position.Z);
        Rlgl.Rotatef(angle, axis.X, axis.Y, axis.Z);
        Rlgl.Scalef(Scale, Scale, Scale);

        Raylib.DrawCubeV(new Vector3(0f, 0f, 0f), new Vector3(0.6f, 1.6f, 0.6f), new Color(228, 232, 240, 255));
        Raylib.DrawCubeV(new Vector3(0f, 1.05f, 0f), new Vector3(0.42f, 0.5f, 0.42f), Accent);
        Raylib.DrawCubeV(new Vector3(0f, -0.95f, 0f), new Vector3(0.34f, 0.3f, 0.34f), new Color(90, 96, 110, 255));

        // Fins
        Raylib.DrawCubeV(new Vector3(0.42f, -0.6f, 0f), new Vector3(0.3f, 0.5f, 0.12f), Accent);
        Raylib.DrawCubeV(new Vector3(-0.42f, -0.6f, 0f), new Vector3(0.3f, 0.5f, 0.12f), Accent);
        Raylib.DrawCubeV(new Vector3(0f, -0.6f, 0.42f), new Vector3(0.12f, 0.5f, 0.3f), Accent);
        Raylib.DrawCubeV(new Vector3(0f, -0.6f, -0.42f), new Vector3(0.12f, 0.5f, 0.3f), Accent);

        Rlgl.PopMatrix();
    }
}
