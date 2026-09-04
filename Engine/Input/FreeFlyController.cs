using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;

namespace VoxelEngine.Input;

/// <summary>
/// One frame of pilot input, so the flight model can be stepped without a window. <see cref="Move"/>
/// is in the ship's own axes: X right, Y up, Z forward.
/// </summary>
public readonly record struct FlightInput(Vector2 Look, Vector3 Move, float Roll, bool Boost)
{
    public static FlightInput None => new(Vector2.Zero, Vector3.Zero, 0f, false);

    /// <summary>Mouse and keyboard: WASD along the view, Space and Ctrl up and down, Q and E roll, Shift boosts</summary>
    public static FlightInput Read()
    {
        Vector3 move = Vector3.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W)) move.Z += 1f;
        if (Raylib.IsKeyDown(KeyboardKey.S)) move.Z -= 1f;
        if (Raylib.IsKeyDown(KeyboardKey.D)) move.X += 1f;
        if (Raylib.IsKeyDown(KeyboardKey.A)) move.X -= 1f;
        if (Raylib.IsKeyDown(KeyboardKey.Space)) move.Y += 1f;
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl)) move.Y -= 1f;

        float roll = 0f;
        if (Raylib.IsKeyDown(KeyboardKey.E)) roll += 1f;
        if (Raylib.IsKeyDown(KeyboardKey.Q)) roll -= 1f;

        return new FlightInput(Raylib.GetMouseDelta(), move, roll, Raylib.IsKeyDown(KeyboardKey.LeftShift));
    }
}

/// <summary>
/// Flight with all six degrees of freedom, the way a ship moves in space: there is no horizon
/// and no up. The mouse pitches and yaws around the ship's own axes, so pulling up long enough
/// takes you round in a loop; Q and E roll; WASD, Space and Ctrl push along the ship's axes.
/// Thrust feeds a velocity that only decays through damping, so letting go coasts on instead of
/// stopping dead.
/// </summary>
public sealed class FreeFlyController
{
    private readonly EngineSettings _settings;

    private Quaternion _orientation = Quaternion.Identity;
    private Vector3 _velocity;
    private float _fov;

    public Vector3 Position;

    /// <summary>Thrust in metres per second squared</summary>
    public float Thrust { get; set; } = 90f;

    public float BoostMultiplier { get; set; } = 3.5f;

    /// <summary>Share of the velocity shed per second (0 = frictionless, which is what an orbit needs)</summary>
    public float Damping { get; set; } = 2.2f;

    /// <summary>Runaway guard rather than a speed limit: gravity may push right up against it</summary>
    public float MaxSpeed { get; set; } = 260f;

    /// <summary>Roll rate on Q and E, in degrees per second</summary>
    public float RollSpeed { get; set; } = 80f;

    /// <summary>
    /// Acceleration from outside the ship, gravity above all. Set it once per frame before
    /// <see cref="Update"/>; it is applied like thrust and is what makes an orbit possible.
    /// </summary>
    public Vector3 ExternalAcceleration { get; set; }

    public Vector3 Velocity => _velocity;
    public float Speed => _velocity.Length();

    public bool Boosting { get; private set; }

    public FreeFlyController(Vector3 start, EngineSettings settings)
    {
        _settings = settings;
        Position = start;
        _fov = settings.FieldOfView;
    }

    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, _orientation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, _orientation);

    /// <summary>Screen right; the same handedness as raylib's camera</summary>
    public Vector3 Right => Vector3.Cross(Forward, Up);

    /// <summary>Kills the motion without changing where you are looking</summary>
    public void Halt() => _velocity = Vector3.Zero;

    /// <summary>Sets the motion outright, for a launch off a moving surface</summary>
    public void SetVelocity(Vector3 velocity) => _velocity = velocity;

    /// <summary>
    /// Aim the view at a point in the world, for a scripted start or a jump cut. The roll is
    /// settled so that <paramref name="up"/> (the world's Y by default) points up on screen.
    /// </summary>
    public void PointAt(Vector3 target, Vector3? up = null)
    {
        Vector3 direction = target - Position;
        if (direction.LengthSquared() < 1e-8f) return;

        LookAlong(Vector3.Normalize(direction), up ?? Vector3.UnitY);
    }

    public void Teleport(Vector3 position)
    {
        Position = position;
        _velocity = Vector3.Zero;
    }

    public Camera3D Update(float dt) => Step(FlightInput.Read(), dt);

    /// <summary>One step of the flight model on explicit input</summary>
    public Camera3D Step(in FlightInput input, float dt)
    {
        float sensitivity = _settings.MouseSensitivity * (MathF.PI / 180f);

        // Every turn is about the ship's own axes, in the order yaw, pitch, roll: mouse right
        // turns right whichever way up the ship happens to be
        Rotate(Up, -input.Look.X * sensitivity);
        Rotate(Right, -input.Look.Y * sensitivity);
        Rotate(Forward, input.Roll * RollSpeed * (MathF.PI / 180f) * dt);

        Vector3 wish = Right * input.Move.X + Up * input.Move.Y + Forward * input.Move.Z;
        Boosting = input.Boost && wish.LengthSquared() > 0f;

        if (wish.LengthSquared() > 0f)
        {
            float thrust = Thrust * (Boosting ? BoostMultiplier : 1f);
            _velocity += Vector3.Normalize(wish) * thrust * dt;
        }

        _velocity += ExternalAcceleration * dt;
        _velocity *= MathF.Max(0f, 1f - Damping * dt);

        float speed = _velocity.Length();
        float maxSpeed = MaxSpeed * (Boosting ? BoostMultiplier : 1f);
        if (speed > maxSpeed) _velocity *= maxSpeed / speed;

        Position += _velocity * dt;

        // The field of view widens with speed, which sells the pace in empty space
        float targetFov = _settings.FieldOfView + Math.Clamp(speed / 12f, 0f, 18f);
        _fov += (targetFov - _fov) * MathF.Min(1f, 4f * dt);

        return new Camera3D
        {
            Position = Position,
            Target = Position + Forward,
            Up = Up,
            FovY = _fov,
            Projection = CameraProjection.Perspective,
        };
    }

    private void Rotate(Vector3 axis, float radians)
    {
        if (radians == 0f) return;

        Quaternion turn = Quaternion.CreateFromAxisAngle(axis, radians);
        _orientation = Quaternion.Normalize(Quaternion.Concatenate(_orientation, turn));
    }

    private void LookAlong(Vector3 forward, Vector3 upHint)
    {
        Vector3 right = Vector3.Cross(forward, upHint);
        if (right.LengthSquared() < 1e-6f) right = Vector3.Cross(forward, Vector3.UnitX); // looking straight along the hint
        right = Vector3.Normalize(right);

        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        // Rows are the images of the axes: the ship's X goes to screen left, Y up, Z forward
        var basis = new Matrix4x4(
            -right.X, -right.Y, -right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);

        _orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }
}
