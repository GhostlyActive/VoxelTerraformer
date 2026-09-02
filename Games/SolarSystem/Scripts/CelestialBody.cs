using System.Numerics;
using VoxelEngine.World;

namespace Terraformer.Games.SolarSystem;

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

    public required VoxelBody Body { get; init; }

    /// <summary>The body being orbited; null means the sun at the origin</summary>
    public CelestialBody? Parent { get; init; }

    public float OrbitRadius { get; init; }

    /// <summary>Angular velocity in radians per second</summary>
    public float OrbitSpeed { get; init; }

    /// <summary>Tilt of the orbital plane against the XZ plane, in radians</summary>
    public float OrbitTilt { get; init; }

    public float OrbitPhase { get; set; }

    public string Name => Body.Name;

    /// <summary>How much of the body is still standing: 1 = untouched, 0 = taken apart</summary>
    public float Integrity => _initialVoxels == 0 ? 0f : _solidVoxels / (float)_initialVoxels;

    /// <summary>Call once after filling the body: fixes the reference value for the damage readout</summary>
    public void TakeCensus()
    {
        _initialVoxels = Body.CountSolid();
        _solidVoxels = _initialVoxels;
    }

    /// <summary>Book the voxels that were shot away instead of recounting the whole body</summary>
    public void RegisterCarve(int removedVoxels) => _solidVoxels = Math.Max(0, _solidVoxels - removedVoxels);

    public void Advance(float dt)
    {
        OrbitPhase += OrbitSpeed * dt;

        Vector3 center = Parent?.Body.Position ?? Vector3.Zero;

        float x = MathF.Cos(OrbitPhase) * OrbitRadius;
        float z = MathF.Sin(OrbitPhase) * OrbitRadius;

        // Tilt the orbital plane about X: flat circles in the XZ plane would look like a diagram
        Body.Position = center + new Vector3(x, z * MathF.Sin(OrbitTilt), z * MathF.Cos(OrbitTilt));
        Body.Advance(dt);
    }
}
