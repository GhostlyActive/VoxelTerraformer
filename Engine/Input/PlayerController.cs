using Raylib_cs;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.World;

namespace VoxelEngine.Input;

/// <summary>
/// One frame of walking input, so the player can be stepped without a window. <see cref="Move"/>
/// is X right and Y forward, in the player's own heading.
/// </summary>
public readonly record struct PlayerInput(Vector2 Look, Vector2 Move, bool Sprint, bool JumpPressed, bool JumpHeld)
{
    public static PlayerInput None => new(Vector2.Zero, Vector2.Zero, false, false, false);

    /// <summary>Mouse and keyboard: WASD walks, Shift sprints, Space jumps and, held in the air, fires the jetpack</summary>
    public static PlayerInput Read()
    {
        Vector2 move = Vector2.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W)) move.Y += 1f;
        if (Raylib.IsKeyDown(KeyboardKey.S)) move.Y -= 1f;
        if (Raylib.IsKeyDown(KeyboardKey.D)) move.X += 1f;
        if (Raylib.IsKeyDown(KeyboardKey.A)) move.X -= 1f;

        return new PlayerInput(
            Raylib.GetMouseDelta(),
            move,
            Raylib.IsKeyDown(KeyboardKey.LeftShift),
            Raylib.IsKeyPressed(KeyboardKey.Space),
            Raylib.IsKeyDown(KeyboardKey.Space));
    }
}

/// <summary>
/// The walking player: mouse look, WASD, sprint, a jump with coyote time and a jump buffer, and
/// sub-voxel collision against the world. With <see cref="JetpackEnabled"/>, holding the jump
/// key in the air burns fuel for lift; the tank refills on the ground.
/// </summary>
public class PlayerController
{
    public Vector3 Position;
    private Vector3 _velocity;

    // Look
    private float _yaw;   // left/right
    private float _pitch; // up/down

    // Player body (AABB)
    private const float HalfWidth = 0.30f;
    private const float Height = 1.80f;
    private const float EyeHeight = 1.62f;

    // Movement tuning comes from the scene's TerrainSettings, mouse and view from the engine's; both change live
    private readonly EngineSettings _engine;
    private readonly TerrainSettings _settings;

    // The field of view widens a little while sprinting, which sells the pace
    private const float SprintFovBoost = 6f;
    private float _fov;

    // Jump feel: a jump still counts shortly after walking off an edge (coyote time), and a press
    // that comes in slightly too early is buffered until landing
    private const float CoyoteTime = 0.12f;
    private const float JumpBufferTime = 0.15f;

    private bool _grounded;
    private float _timeSinceGrounded = 999f;
    private float _timeSinceJumpPressed = 999f;

    // The jetpack must not turn a jump into a rocket start: it only fires once the coyote time
    // is over, so a tap of Space is still just a jump
    private const float JetpackRefillSeconds = 4f;
    private const float JetpackMaxRise = 32f;
    private float _fuel = 1f;

    public bool IsGrounded => _grounded;

    /// <summary>Lets the jump key, held in the air, fire the jetpack; off, the player only jumps</summary>
    public bool JetpackEnabled { get; set; }

    /// <summary>Fuel left in the tank, 0..1</summary>
    public float Fuel01 => _fuel;

    /// <summary>True in the frames the jetpack is firing, for exhaust and sound</summary>
    public bool JetpackBurning { get; private set; }

    public BoundingBox Bounds => new(
        new Vector3(Position.X - HalfWidth, Position.Y, Position.Z - HalfWidth),
        new Vector3(Position.X + HalfWidth, Position.Y + Height, Position.Z + HalfWidth));

    /// <summary>Moves the player outright, for instance after loading a save</summary>
    public void Teleport(Vector3 position)
    {
        Position = position;
        _velocity = Vector3.Zero;
    }

    public PlayerController(Vector3 startPos, EngineSettings engine, TerrainSettings settings)
    {
        _engine = engine;
        _settings = settings;
        Position = startPos;
        _yaw = 135f;
        _pitch = -15f;
    }

    public Camera3D Update(VoxelWorld world, float dt) => Step(world, PlayerInput.Read(), dt);

    /// <summary>One step of the walking model on explicit input</summary>
    public Camera3D Step(VoxelWorld world, in PlayerInput input, float dt)
    {
        UpdateLook(input.Look);

        Vector2 wish = input.Move;
        if (wish.LengthSquared() > 0f)
            wish = Vector2.Normalize(wish);

        // Convert wish direction to world space based on yaw
        Vector3 forward = ForwardOnXZ();
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

        bool sprinting = input.Sprint && wish.LengthSquared() > 0f;
        float speed = _settings.WalkSpeed * (sprinting ? _settings.SprintMultiplier : 1f);
        Vector3 move = (right * wish.X + forward * wish.Y) * speed;

        // Apply horizontal velocity (simple “arcade”)
        _velocity.X = move.X;
        _velocity.Z = move.Z;

        // Jump (with coyote time and jump buffer)
        _timeSinceJumpPressed += dt;
        if (input.JumpPressed) _timeSinceJumpPressed = 0f;

        if (_grounded) _timeSinceGrounded = 0f;
        else _timeSinceGrounded += dt;

        bool canJump = _grounded || _timeSinceGrounded < CoyoteTime;
        if (canJump && _timeSinceJumpPressed < JumpBufferTime)
        {
            _velocity.Y = _settings.JumpSpeed;
            _grounded = false;
            _timeSinceGrounded = CoyoteTime;     // coyote time spent: no second jump out of mid-air
            _timeSinceJumpPressed = JumpBufferTime; // buffer spent
        }

        UpdateJetpack(input.JumpHeld, dt);

        // Gravity
        _velocity.Y -= _settings.Gravity * dt;
        if (_velocity.Y < -60f) _velocity.Y = -60f;

        // Move & collide (axis separated)
        MoveAndCollide(world, dt);

        // Build camera from player
        float targetFov = _engine.FieldOfView + (sprinting ? SprintFovBoost : 0f);
        if (_fov <= 0f) _fov = targetFov; // first frame: do not ramp up from nothing
        _fov += (targetFov - _fov) * Math.Min(1f, 10f * dt);

        return BuildCamera();
    }

    private void UpdateJetpack(bool jumpHeld, float dt)
    {
        JetpackBurning = false;
        if (!JetpackEnabled) return;

        if (_grounded)
        {
            _fuel = MathF.Min(1f, _fuel + dt / JetpackRefillSeconds);
            return;
        }

        bool airborne = _timeSinceGrounded >= CoyoteTime;
        if (!airborne || !jumpHeld || _fuel <= 0f) return;

        _fuel = MathF.Max(0f, _fuel - dt / MathF.Max(0.1f, _settings.JetpackFuelSeconds));
        _velocity.Y = MathF.Min(JetpackMaxRise, _velocity.Y + _settings.JetpackThrust * dt);
        JetpackBurning = true;
    }

    /// <summary>Camera at the current position and heading, without physics</summary>
    public Camera3D CameraOnly()
    {
        if (_fov <= 0f) _fov = _engine.FieldOfView;
        return BuildCamera();
    }

    /// <summary>Aim the view at a point in the world instead of using the mouse</summary>
    public void PointAt(Vector3 target)
    {
        Vector3 eye = Position + new Vector3(0, EyeHeight, 0);
        Vector3 direction = target - eye;

        float horizontal = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (horizontal < 1e-4f && MathF.Abs(direction.Y) < 1e-4f) return;

        _yaw = MathF.Atan2(direction.X, direction.Z) * (180f / MathF.PI);
        _pitch = Math.Clamp(MathF.Atan2(direction.Y, horizontal) * (180f / MathF.PI), -89f, 89f);
    }

    private Camera3D BuildCamera()
    {
        Vector3 eye = Position + new Vector3(0, EyeHeight, 0);

        return new Camera3D
        {
            Position = eye,
            Target = eye + LookDirection(),
            Up = Vector3.UnitY,
            FovY = _fov,
            Projection = CameraProjection.Perspective
        };
    }

    private void UpdateLook(Vector2 look)
    {
        float sensitivity = _engine.MouseSensitivity;

        _yaw -= look.X * sensitivity;
        _pitch -= look.Y * sensitivity;

        _pitch = Math.Clamp(_pitch, -89f, 89f);
        if (_yaw > 360f) _yaw -= 360f;
        if (_yaw < 0f) _yaw += 360f;
    }

    private Vector3 LookDirection()
    {
        float yawRad = _yaw * (MathF.PI / 180f);
        float pitchRad = _pitch * (MathF.PI / 180f);

        float cy = MathF.Cos(yawRad);
        float sy = MathF.Sin(yawRad);
        float cp = MathF.Cos(pitchRad);
        float sp = MathF.Sin(pitchRad);

        // Raylib uses right-handed; this works fine for FPS
        return Vector3.Normalize(new Vector3(sy * cp, sp, cy * cp));
    }

    private Vector3 ForwardOnXZ()
    {
        float yawRad = _yaw * (MathF.PI / 180f);
        return Vector3.Normalize(new Vector3(MathF.Sin(yawRad), 0f, MathF.Cos(yawRad)));
    }

    private void MoveAndCollide(VoxelWorld world, float dt)
    {
        _grounded = false;

        // X
        Position.X += _velocity.X * dt;
        ResolveCollisions(world, ref Position, ref _velocity, axis: 0);

        // Z
        Position.Z += _velocity.Z * dt;
        ResolveCollisions(world, ref Position, ref _velocity, axis: 2);

        // Y
        Position.Y += _velocity.Y * dt;
        float oldVy = _velocity.Y;
        ResolveCollisions(world, ref Position, ref _velocity, axis: 1);

        // grounded if we collided while moving down
        if (oldVy < 0f && _velocity.Y == 0f)
            _grounded = true;
    }

    /// <summary>
    /// axis: 0=x, 1=y, 2=z
    /// Push player out of solid geometry and zero that velocity axis.
    /// Carved blocks (Sculpt) collide at sub-voxel level.
    /// ALL overlapping boxes are collected first and the correction is applied exactly once:
    /// correcting per box would let the first one zero the velocity, leaving the player stuck
    /// inside the remaining overlapping boxes.
    /// </summary>
    private void ResolveCollisions(VoxelWorld world, ref Vector3 pos, ref Vector3 vel, int axis)
    {
        int ix0 = (int)MathF.Floor(pos.X - HalfWidth);
        int ix1 = (int)MathF.Floor(pos.X + HalfWidth);
        int iy0 = (int)MathF.Floor(pos.Y);
        int iy1 = (int)MathF.Floor(pos.Y + Height);
        int iz0 = (int)MathF.Floor(pos.Z - HalfWidth);
        int iz1 = (int)MathF.Floor(pos.Z + HalfWidth);

        bool hit = false;
        float minBound = float.MaxValue; // lowest edge of all overlapping boxes (on this axis)
        float maxBound = float.MinValue; // highest edge

        for (int x = ix0; x <= ix1; x++)
        for (int y = iy0; y <= iy1; y++)
        for (int z = iz0; z <= iz1; z++)
        {
            if (!BlockRegistry.IsSolid(world.GetBlock(x, y, z))) continue;

            if (world.TryGetRefinement(x, y, z, out byte[] field))
            {
                const float cell = SubVoxels.CellSize;
                for (int sz = 0; sz < SubVoxels.Divisions; sz++)
                for (int sy = 0; sy < SubVoxels.Divisions; sy++)
                for (int sx = 0; sx < SubVoxels.Divisions; sx++)
                {
                    if (!SubVoxels.IsSolid(field, sx, sy, sz)) continue;
                    AccumulateBox(in pos, x + sx * cell, y + sy * cell, z + sz * cell, cell, axis,
                        ref hit, ref minBound, ref maxBound);
                }
            }
            else
            {
                AccumulateBox(in pos, x, y, z, 1f, axis, ref hit, ref minBound, ref maxBound);
            }
        }

        if (!hit) return;

        if (axis == 0)
        {
            if (vel.X > 0f) pos.X = minBound - HalfWidth - 0.0001f;
            else if (vel.X < 0f) pos.X = maxBound + HalfWidth + 0.0001f;
            vel.X = 0f;
        }
        else if (axis == 2)
        {
            if (vel.Z > 0f) pos.Z = minBound - HalfWidth - 0.0001f;
            else if (vel.Z < 0f) pos.Z = maxBound + HalfWidth + 0.0001f;
            vel.Z = 0f;
        }
        else // Y
        {
            if (vel.Y > 0f) pos.Y = minBound - Height - 0.0001f;
            else if (vel.Y < 0f) pos.Y = maxBound + 0.0001f;
            vel.Y = 0f;
        }
    }

    // Collects the axis bounds of a solid box if it overlaps the player
    private void AccumulateBox(in Vector3 pos, float bMinX, float bMinY, float bMinZ, float size, int axis,
        ref bool hit, ref float minBound, ref float maxBound)
    {
        float bMaxX = bMinX + size;
        float bMaxY = bMinY + size;
        float bMaxZ = bMinZ + size;

        if (pos.X + HalfWidth <= bMinX || pos.X - HalfWidth >= bMaxX ||
            pos.Y + Height <= bMinY || pos.Y >= bMaxY ||
            pos.Z + HalfWidth <= bMinZ || pos.Z - HalfWidth >= bMaxZ)
            return;

        hit = true;

        if (axis == 0)
        {
            minBound = MathF.Min(minBound, bMinX);
            maxBound = MathF.Max(maxBound, bMaxX);
        }
        else if (axis == 2)
        {
            minBound = MathF.Min(minBound, bMinZ);
            maxBound = MathF.Max(maxBound, bMaxZ);
        }
        else
        {
            minBound = MathF.Min(minBound, bMinY);
            maxBound = MathF.Max(maxBound, bMaxY);
        }
    }
}
