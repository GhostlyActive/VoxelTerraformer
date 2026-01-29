using Raylib_cs;
using System.Numerics;
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

    // Movement tuning
    private const float MoveSpeed = 7.0f;
    private const float JumpSpeed = 10.2f;
    private const float Gravity = 18.0f;

    private bool _grounded;

    public PlayerController(Vector3 startPos)
    {
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

        Vector3 move = (right * wish.X + forward * wish.Z) * MoveSpeed;

        // Apply horizontal velocity (simple “arcade”)
        _velocity.X = move.X;
        _velocity.Z = move.Z;

        // Jump
        if (_grounded && Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            _velocity.Y = JumpSpeed;
            _grounded = false;
        }

        // Gravity
        _velocity.Y -= Gravity * dt;
        if (_velocity.Y < -60f) _velocity.Y = -60f;

        // Move & collide (axis separated)
        MoveAndCollide(world, dt);

        // Build camera from player
        Vector3 eye = Position + new Vector3(0, EyeHeight, 0);
        Vector3 dir = LookDirection();
        Camera3D cam = new Camera3D
        {
            Position = eye,
            Target = eye + dir,
            Up = Vector3.UnitY,
            FovY = 60f,
            Projection = CameraProjection.Perspective
        };
        return cam;
    }

    private void UpdateLook(float dt)
    {
        // Mouse delta
        Vector2 md = Raylib.GetMouseDelta();
        const float sensitivity = 0.12f;

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
    /// Push player out of solid blocks and zero that velocity axis.
    /// </summary>
    private void ResolveCollisions(VoxelWorld world, ref Vector3 pos, ref Vector3 vel, int axis)
    {
        // Player AABB in world space
        float minX = pos.X - HalfWidth;
        float maxX = pos.X + HalfWidth;
        float minY = pos.Y;
        float maxY = pos.Y + Height;
        float minZ = pos.Z - HalfWidth;
        float maxZ = pos.Z + HalfWidth;

        int ix0 = (int)MathF.Floor(minX);
        int ix1 = (int)MathF.Floor(maxX);
        int iy0 = (int)MathF.Floor(minY);
        int iy1 = (int)MathF.Floor(maxY);
        int iz0 = (int)MathF.Floor(minZ);
        int iz1 = (int)MathF.Floor(maxZ);

        bool hit = false;

        for (int x = ix0; x <= ix1; x++)
        for (int y = iy0; y <= iy1; y++)
        for (int z = iz0; z <= iz1; z++)
        {
            if (world.GetBlock(x, y, z) == 0) continue;

            // Block AABB: [x,x+1] etc.
            float bMinX = x, bMaxX = x + 1f;
            float bMinY = y, bMaxY = y + 1f;
            float bMinZ = z, bMaxZ = z + 1f;

            // Overlap?
            if (maxX <= bMinX || minX >= bMaxX ||
                maxY <= bMinY || minY >= bMaxY ||
                maxZ <= bMinZ || minZ >= bMaxZ)
                continue;

            hit = true;

            // push out on the moved axis only
            if (axis == 0)
            {
                if (vel.X > 0f) pos.X = bMinX - HalfWidth - 0.0001f;
                else if (vel.X < 0f) pos.X = bMaxX + HalfWidth + 0.0001f;
                vel.X = 0f;
            }
            else if (axis == 2)
            {
                if (vel.Z > 0f) pos.Z = bMinZ - HalfWidth - 0.0001f;
                else if (vel.Z < 0f) pos.Z = bMaxZ + HalfWidth + 0.0001f;
                vel.Z = 0f;
            }
            else // Y
            {
                if (vel.Y > 0f) pos.Y = bMinY - Height - 0.0001f;
                else if (vel.Y < 0f) pos.Y = bMaxY + 0.0001f;
                vel.Y = 0f;
            }

            // update AABB after correction for further checks
            minX = pos.X - HalfWidth; maxX = pos.X + HalfWidth;
            minY = pos.Y;            maxY = pos.Y + Height;
            minZ = pos.Z - HalfWidth; maxZ = pos.Z + HalfWidth;
        }

        // (Optional) If we get stuck due to weird overlaps, you could iterate a few times,
        // but for voxel worlds axis separation is usually enough.
        _ = hit;
    }
}
