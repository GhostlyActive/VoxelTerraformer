using System.Numerics;
using VoxelEngine.World;

namespace Terraformer.Games.SolarSystem;

/// <summary>
/// Ein Himmelskörper: ein <see cref="VoxelBody"/> auf einer Kreisbahn. Planeten kreisen um die
/// Sonne im Ursprung, Monde um ihren Planeten — deshalb kennt jeder Körper optional einen
/// <see cref="Parent"/>, dessen aktuelle Position der Mittelpunkt seiner Bahn ist.
///
/// Die Reihenfolge beim Fortschreiben zählt: ein Mond muss nach seinem Planeten laufen, sonst
/// hängt er dem Planeten ein Bild hinterher.
/// </summary>
public sealed class CelestialBody
{
    private int _initialVoxels;
    private int _solidVoxels;

    public required VoxelBody Body { get; init; }

    /// <summary>Körper, um den gekreist wird; null heißt: um die Sonne im Ursprung</summary>
    public CelestialBody? Parent { get; init; }

    public float OrbitRadius { get; init; }

    /// <summary>Winkelgeschwindigkeit in Radiant pro Sekunde</summary>
    public float OrbitSpeed { get; init; }

    /// <summary>Neigung der Bahnebene gegen die XZ-Ebene, in Radiant</summary>
    public float OrbitTilt { get; init; }

    public float OrbitPhase { get; set; }

    public string Name => Body.Name;

    /// <summary>Anteil des Körpers, der noch steht: 1 = unversehrt, 0 = restlos zerlegt</summary>
    public float Integrity => _initialVoxels == 0 ? 0f : _solidVoxels / (float)_initialVoxels;

    /// <summary>Nach dem Füllen einmal aufrufen — legt den Bezugswert der Zerstörungsanzeige fest</summary>
    public void TakeCensus()
    {
        _initialVoxels = Body.CountSolid();
        _solidVoxels = _initialVoxels;
    }

    /// <summary>Weggeschossene Voxel verbuchen, statt den ganzen Körper neu zu zählen</summary>
    public void RegisterCarve(int removedVoxels) => _solidVoxels = Math.Max(0, _solidVoxels - removedVoxels);

    public void Advance(float dt)
    {
        OrbitPhase += OrbitSpeed * dt;

        Vector3 center = Parent?.Body.Position ?? Vector3.Zero;

        float x = MathF.Cos(OrbitPhase) * OrbitRadius;
        float z = MathF.Sin(OrbitPhase) * OrbitRadius;

        // Bahnebene um die X-Achse kippen: reine Kreise in der XZ-Ebene sähen wie ein Diagramm aus
        Body.Position = center + new Vector3(x, z * MathF.Sin(OrbitTilt), z * MathF.Cos(OrbitTilt));
        Body.Advance(dt);
    }
}
