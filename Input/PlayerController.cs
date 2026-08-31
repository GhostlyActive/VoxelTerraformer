using Raylib_cs;
using System.Numerics;
using Terraformer.Config;
using Terraformer.World;

namespace Terraformer;

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

    // Movement-Tuning kommt aus den DebugSettings (Menü auf M) und ist live änderbar
    private readonly DebugSettings _settings;

    // FOV zieht beim Sprinten leicht auf — verkauft das Tempo spürbar
    private const float BaseFov = 60f;
    private const float SprintFov = 66f;
    private float _fov = BaseFov;

    // Sprung-Feel: kurz nach Kantenabgang darf noch gesprungen werden (Coyote),
    // und ein knapp zu früher Druck wird bis zur Landung gepuffert
    private const float CoyoteTime = 0.12f;
    private const float JumpBufferTime = 0.15f;

    private bool _grounded;
    private float _timeSinceGrounded = 999f;
    private float _timeSinceJumpPressed = 999f;

    public bool IsGrounded => _grounded;

    public BoundingBox Bounds => new(
        new Vector3(Position.X - HalfWidth, Position.Y, Position.Z - HalfWidth),
        new Vector3(Position.X + HalfWidth, Position.Y + Height, Position.Z + HalfWidth));

    /// <summary>Setzt den Spieler hart um (z. B. nach dem Laden eines Spielstands)</summary>
    public void Teleport(Vector3 position)
    {
        Position = position;
        _velocity = Vector3.Zero;
    }

    public PlayerController(Vector3 startPos, DebugSettings settings)
    {
        _settings = settings;
        Position = startPos;
        _yaw = 135f;
        _pitch = -15f;
    }

    public Camera3D Update(VoxelWorld world, float dt)
    {
        UpdateLook(dt);

        // Input movement in local space
        Vector3 wish = Vector3.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W)) wish += Vector3.UnitZ;
        if (Raylib.IsKeyDown(KeyboardKey.S)) wish -= Vector3.UnitZ;
        if (Raylib.IsKeyDown(KeyboardKey.D)) wish += Vector3.UnitX;
        if (Raylib.IsKeyDown(KeyboardKey.A)) wish -= Vector3.UnitX;

        if (wish.LengthSquared() > 0f)
            wish = Vector3.Normalize(wish);

        // Convert wish direction to world space based on yaw
        Vector3 forward = ForwardOnXZ();
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

        bool sprinting = Raylib.IsKeyDown(KeyboardKey.LeftShift) && wish.LengthSquared() > 0f;
        float speed = _settings.WalkSpeed * (sprinting ? _settings.SprintMultiplier : 1f);
        Vector3 move = (right * wish.X + forward * wish.Z) * speed;

        // Apply horizontal velocity (simple “arcade”)
        _velocity.X = move.X;
        _velocity.Z = move.Z;

        // Jump (mit Coyote-Time und Jump-Buffer)
        _timeSinceJumpPressed += dt;
        if (Raylib.IsKeyPressed(KeyboardKey.Space)) _timeSinceJumpPressed = 0f;

        if (_grounded) _timeSinceGrounded = 0f;
        else _timeSinceGrounded += dt;

        bool canJump = _grounded || _timeSinceGrounded < CoyoteTime;
        if (canJump && _timeSinceJumpPressed < JumpBufferTime)
        {
            _velocity.Y = _settings.JumpSpeed;
            _grounded = false;
            _timeSinceGrounded = CoyoteTime;     // Coyote verbraucht — kein zweiter Sprung aus der Luft
            _timeSinceJumpPressed = JumpBufferTime; // Buffer verbraucht
        }

        // Gravity
        _velocity.Y -= _settings.Gravity * dt;
        if (_velocity.Y < -60f) _velocity.Y = -60f;

        // Move & collide (axis separated)
        MoveAndCollide(world, dt);

        // Build camera from player
        float targetFov = sprinting ? SprintFov : BaseFov;
        _fov += (targetFov - _fov) * Math.Min(1f, 10f * dt);

        Vector3 eye = Position + new Vector3(0, EyeHeight, 0);
        Vector3 dir = LookDirection();
        Camera3D cam = new Camera3D
        {
            Position = eye,
            Target = eye + dir,
            Up = Vector3.UnitY,
            FovY = _fov,
            Projection = CameraProjection.Perspective
        };
        return cam;
    }

    private void UpdateLook(float dt)
    {
        // Mouse delta
        Vector2 md = Raylib.GetMouseDelta();
        float sensitivity = _settings.MouseSensitivity;

        _yaw -= md.X * sensitivity;
        _pitch -= md.Y * sensitivity;

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
    /// Angeschnitzte Blöcke (Sculpt) kollidieren auf Sub-Voxel-Ebene.
    /// Erst werden ALLE überlappenden Boxen eingesammelt, dann wird genau einmal
    /// korrigiert — würde pro Box korrigiert, nullt die erste Box die Geschwindigkeit
    /// und der Spieler bliebe in weiteren überlappenden Boxen stecken.
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
        float minBound = float.MaxValue; // kleinste Unterkante aller überlappenden Boxen (auf der Achse)
        float maxBound = float.MinValue; // größte Oberkante

        for (int x = ix0; x <= ix1; x++)
        for (int y = iy0; y <= iy1; y++)
        for (int z = iz0; z <= iz1; z++)
        {
            if (!BlockRegistry.IsSolid(world.GetBlock(x, y, z))) continue;

            if (world.TryGetRefinement(x, y, z, out ulong[] mask))
            {
                const float cell = SubVoxels.CellSize;
                for (int sz = 0; sz < SubVoxels.Divisions; sz++)
                for (int sy = 0; sy < SubVoxels.Divisions; sy++)
                for (int sx = 0; sx < SubVoxels.Divisions; sx++)
                {
                    if (!SubVoxels.HasBit(mask, sx, sy, sz)) continue;
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

    // Sammelt die Achsen-Grenzen einer soliden Box ein, falls sie den Spieler überlappt
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
