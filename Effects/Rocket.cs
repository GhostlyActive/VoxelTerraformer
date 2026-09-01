using Raylib_cs;
using System.Numerics;

namespace Terraformer.Effects;

/// <summary>
/// Rakete für den Demo-Ablauf: fliegt eine gescriptete Bahn (quadratische Bézier), damit
/// Startpunkt, Scheitel und Aufschlag exakt vorhersagbar sind — eine physikalisch simulierte
/// Bahn würde bei jeder Aufnahme woanders landen. Der Körper besteht aus Würfeln und kippt
/// in Flugrichtung.
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

    /// <summary>Aufschlag erreicht — der Aufrufer löst dann die Explosion aus</summary>
    public bool Landed => _elapsed >= _flightSeconds;

    public Rocket(Vector3 start, Vector3 apex, Vector3 target, float flightSeconds)
    {
        _start = start;
        _target = target;
        _flightSeconds = flightSeconds;

        // Kontrollpunkt so, dass die Kurve tatsächlich durch den gewünschten Scheitel läuft
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

        // Rauch sitzt am Heck und bekommt einen Rückstoß entgegen der Flugrichtung
        Vector3 nozzle = Position - Direction * 0.9f;
        particles.SpawnSmoke(nozzle, -Direction * 3.5f, 3);
    }

    public void Draw()
    {
        if (Landed) return;

        // Um die Flugrichtung drehen: Standardachse des Körpers ist +Y
        Vector3 axis = Vector3.Cross(Vector3.UnitY, Direction);
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, Direction), -1f, 1f)) * (180f / MathF.PI);
        if (axis.LengthSquared() < 1e-6f) axis = Vector3.UnitX;

        Rlgl.PushMatrix();
        Rlgl.Translatef(Position.X, Position.Y, Position.Z);
        Rlgl.Rotatef(angle, axis.X, axis.Y, axis.Z);

        Raylib.DrawCubeV(new Vector3(0f, 0f, 0f), new Vector3(0.6f, 1.6f, 0.6f), new Color(228, 232, 240, 255));
        Raylib.DrawCubeV(new Vector3(0f, 1.05f, 0f), new Vector3(0.42f, 0.5f, 0.42f), new Color(214, 78, 62, 255));
        Raylib.DrawCubeV(new Vector3(0f, -0.95f, 0f), new Vector3(0.34f, 0.3f, 0.34f), new Color(90, 96, 110, 255));

        // Finnen
        Raylib.DrawCubeV(new Vector3(0.42f, -0.6f, 0f), new Vector3(0.3f, 0.5f, 0.12f), new Color(214, 78, 62, 255));
        Raylib.DrawCubeV(new Vector3(-0.42f, -0.6f, 0f), new Vector3(0.3f, 0.5f, 0.12f), new Color(214, 78, 62, 255));
        Raylib.DrawCubeV(new Vector3(0f, -0.6f, 0.42f), new Vector3(0.12f, 0.5f, 0.3f), new Color(214, 78, 62, 255));
        Raylib.DrawCubeV(new Vector3(0f, -0.6f, -0.42f), new Vector3(0.12f, 0.5f, 0.3f), new Color(214, 78, 62, 255));

        Rlgl.PopMatrix();
    }
}
