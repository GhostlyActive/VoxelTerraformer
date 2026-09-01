using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;

namespace VoxelEngine.Input;

/// <summary>
/// Freie Flugkamera ohne Schwerkraft: Maus dreht, WASD schiebt in Blickrichtung, Space und
/// Strg heben und senken, Shift beschleunigt. Der Schub wirkt auf eine Geschwindigkeit, die
/// nur gedämpft abklingt — losgelassen gleitet man weiter, statt auf der Stelle zu stehen.
/// </summary>
public sealed class FreeFlyController
{
    private readonly EngineSettings _settings;

    private Vector3 _velocity;
    private float _yaw;
    private float _pitch;
    private float _fov;

    public Vector3 Position;

    /// <summary>Schub in Metern pro Sekunde²</summary>
    public float Thrust { get; set; } = 90f;

    public float BoostMultiplier { get; set; } = 3.5f;

    /// <summary>Anteil der Geschwindigkeit, der pro Sekunde abgebaut wird (0 = reibungsfrei)</summary>
    public float Damping { get; set; } = 2.2f;

    public float MaxSpeed { get; set; } = 260f;

    public Vector3 Velocity => _velocity;
    public float Speed => _velocity.Length();

    public bool Boosting { get; private set; }

    public FreeFlyController(Vector3 start, EngineSettings settings, float yaw = 0f, float pitch = 0f)
    {
        _settings = settings;
        Position = start;
        _yaw = yaw;
        _pitch = pitch;
        _fov = settings.FieldOfView;
    }

    public Vector3 Forward
    {
        get
        {
            float yaw = _yaw * (MathF.PI / 180f);
            float pitch = _pitch * (MathF.PI / 180f);

            return Vector3.Normalize(new Vector3(
                MathF.Sin(yaw) * MathF.Cos(pitch),
                MathF.Sin(pitch),
                MathF.Cos(yaw) * MathF.Cos(pitch)));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    /// <summary>Bremst den Flug ab, ohne die Blickrichtung zu ändern</summary>
    public void Halt() => _velocity = Vector3.Zero;

    public void Teleport(Vector3 position)
    {
        Position = position;
        _velocity = Vector3.Zero;
    }

    public Camera3D Update(float dt)
    {
        Vector2 mouse = Raylib.GetMouseDelta();
        _yaw -= mouse.X * _settings.MouseSensitivity;
        _pitch = Math.Clamp(_pitch - mouse.Y * _settings.MouseSensitivity, -89f, 89f);

        Vector3 wish = Vector3.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W)) wish += Forward;
        if (Raylib.IsKeyDown(KeyboardKey.S)) wish -= Forward;
        if (Raylib.IsKeyDown(KeyboardKey.D)) wish += Right;
        if (Raylib.IsKeyDown(KeyboardKey.A)) wish -= Right;
        if (Raylib.IsKeyDown(KeyboardKey.Space)) wish += Vector3.UnitY;
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl)) wish -= Vector3.UnitY;

        Boosting = Raylib.IsKeyDown(KeyboardKey.LeftShift) && wish.LengthSquared() > 0f;

        if (wish.LengthSquared() > 0f)
        {
            float thrust = Thrust * (Boosting ? BoostMultiplier : 1f);
            _velocity += Vector3.Normalize(wish) * thrust * dt;
        }

        _velocity *= MathF.Max(0f, 1f - Damping * dt);

        float speed = _velocity.Length();
        float maxSpeed = MaxSpeed * (Boosting ? BoostMultiplier : 1f);
        if (speed > maxSpeed) _velocity *= maxSpeed / speed;

        Position += _velocity * dt;

        // Sichtfeld zieht mit dem Tempo auf — verkauft die Geschwindigkeit im leeren Raum
        float targetFov = _settings.FieldOfView + Math.Clamp(speed / 12f, 0f, 18f);
        _fov += (targetFov - _fov) * MathF.Min(1f, 4f * dt);

        return new Camera3D
        {
            Position = Position,
            Target = Position + Forward,
            Up = Vector3.UnitY,
            FovY = _fov,
            Projection = CameraProjection.Perspective,
        };
    }
}
