using System.Numerics;
using VoxelEngine.World;

namespace Games.SolarSystem;

/// <summary>
/// A celestial body: a <see cref="VoxelBody"/> on a circular orbit. Planets circle the sun at the
/// origin, moons circle their planet, which is why every body optionally knows a
/// <see cref="Parent"/> whose current position is the centre of its orbit.
///
/// Order matters when advancing them: a moon has to run after its planet, otherwise it trails one
/// frame behind.
/// </summary>
public sealed class CelestialBody
{
    private int _initialVoxels;
    private int _solidVoxels;
    private Vector3 _previousPosition;

    public required VoxelBody Body { get; init; }

    /// <summary>The body being orbited; null means the sun at the origin</summary>
    public CelestialBody? Parent { get; init; }

    public float OrbitRadius { get; init; }

    /// <summary>Angular velocity in radians per second</summary>
    public float OrbitSpeed { get; init; }

    /// <summary>Tilt of the orbital plane against the XZ plane, in radians</summary>
    public float OrbitTilt { get; init; }

    public float OrbitPhase { get; set; }

    /// <summary>Pull at the surface in m/s²; everything else follows from it and the radius</summary>
    public float SurfaceGravity { get; init; } = 40f;

    /// <summary>Material that must not be hit: reaching it sets the whole body off</summary>
    public byte VolatileCore { get; init; } = BlockRegistry.Air;

    public bool HasVolatileCore => VolatileCore != BlockRegistry.Air;

    /// <summary>Blown apart: no longer drawn, no longer hit, but its moons keep their orbits</summary>
    public bool Destroyed { get; private set; }

    /// <summary>Seconds since the core went up, or -1 while it is intact</summary>
    public float DetonationTime { get; private set; } = -1f;

    public bool Detonating => DetonationTime >= 0f;

    /// <summary>How many of the outward carving steps have already run</summary>
    public int DetonationStep { get; set; }

    public string Name => Body.Name;

    /// <summary>Speed the body moves along its orbit, so debris can inherit it</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>How much of the body is still standing: 1 = untouched, 0 = taken apart</summary>
    public float Integrity => _initialVoxels == 0 ? 0f : _solidVoxels / (float)_initialVoxels;

    /// <summary>
    /// Standard gravitational parameter (G·M). Derived from the surface pull so that changing the
    /// size of a body keeps its gravity plausible without a second number to tune.
    /// </summary>
    public float GravitationalParameter => SurfaceGravity * Body.SurfaceRadius * Body.SurfaceRadius;

    /// <summary>Beyond this distance the body's pull is ignored, which keeps flight predictable</summary>
    public float InfluenceRadius => Body.SurfaceRadius * 14f;

    /// <summary>Call once after filling the body: fixes the reference value for the damage readout</summary>
    public void TakeCensus()
    {
        _initialVoxels = Body.CountSolid();
        _solidVoxels = _initialVoxels;
    }

    /// <summary>Book the voxels that were shot away instead of recounting the whole body</summary>
    public void RegisterCarve(int removedVoxels) => _solidVoxels = Math.Max(0, _solidVoxels - removedVoxels);

    public void Detonate()
    {
        if (Detonating || Destroyed) return;
        DetonationTime = 0f;
    }

    public void AdvanceDetonation(float dt) => DetonationTime += dt;

    public void MarkDestroyed()
    {
        Destroyed = true;
        _solidVoxels = 0;
    }

    public void Advance(float dt)
    {
        OrbitPhase += OrbitSpeed * dt;

        Vector3 center = Parent?.Body.Position ?? Vector3.Zero;

        float x = MathF.Cos(OrbitPhase) * OrbitRadius;
        float z = MathF.Sin(OrbitPhase) * OrbitRadius;

        // Tilt the orbital plane about X: flat circles in the XZ plane would look like a diagram
        Vector3 position = center + new Vector3(x, z * MathF.Sin(OrbitTilt), z * MathF.Cos(OrbitTilt));

        Velocity = dt > 0f ? (position - _previousPosition) / dt : Vector3.Zero;
        _previousPosition = position;

        Body.Position = position;
        Body.Advance(dt);
    }

    /// <summary>Pull this body exerts on a point in space, already zero beyond its influence</summary>
    public Vector3 GravityAt(Vector3 point)
    {
        if (Destroyed) return Vector3.Zero;

        Vector3 toBody = Body.Position - point;
        float distance = toBody.Length();

        if (distance > InfluenceRadius || distance < 1e-3f) return Vector3.Zero;

        // Below the surface the pull is capped instead of exploding towards the centre
        float effective = MathF.Max(distance, Body.SurfaceRadius);

        return toBody / distance * (GravitationalParameter / (effective * effective));
    }
}
