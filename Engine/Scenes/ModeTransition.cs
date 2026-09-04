using System.Numerics;
using VoxelEngine.World;

namespace VoxelEngine.Scenes;

/// <summary>
/// The switch between two voxel modes as a visible event: a ring that spreads over the ground
/// from where the player stood, at a steady pace. When the switch rebuilds the world's meshes the
/// ring never runs ahead of the rebuild, so everything inside it is guaranteed to already show
/// the new look; a switch that changes only the tool gets the same ring on a timer, so every
/// press of the key feels alike.
///
/// Pure bookkeeping, no rendering: the scene feeds it the honest radius and draws the result.
/// </summary>
public sealed class ModeTransition
{
    /// <summary>How fast the ring travels over the ground, in metres per second</summary>
    public const float WaveSpeed = 200f;

    private const float FadeInSeconds = 0.15f;
    private const float FadeOutSeconds = 0.5f;
    private const float MaxSeconds = 15f;

    public TerrainMode From { get; }
    public TerrainMode To { get; }
    public Vector3 Origin { get; }

    /// <summary>Does the switch rebuild meshes, so the ring has to wait for them?</summary>
    public bool Remeshes { get; }

    public float Age { get; private set; }
    public float DisplayRadius { get; private set; }

    /// <summary>0..1 visibility of the ring: fades in at the start and out once it has run its course</summary>
    public float Strength { get; private set; }

    /// <summary>The ring has faded out; the scene drops the transition</summary>
    public bool Done { get; private set; }

    private bool _finished;
    private float _finishedFor;

    public ModeTransition(TerrainMode from, TerrainMode to, Vector3 origin, bool remeshes)
    {
        From = from;
        To = to;
        Origin = origin;
        Remeshes = remeshes;
    }

    /// <summary>Width of the glowing band, growing with the radius so it stays visible far out</summary>
    public float Width => 4f + DisplayRadius * 0.03f;

    /// <summary>How far the ring has come, relative to where it ends</summary>
    public float Progress01(float endRadius) => Math.Clamp(DisplayRadius / MathF.Max(endRadius, 1f), 0f, 1f);

    /// <summary>
    /// One frame. <paramref name="truthRadius"/> is how far the rebuild has actually come (infinity
    /// when nothing is pending); <paramref name="endRadius"/> is where the ring may stop, normally
    /// the fog, beyond which nobody would see it.
    /// </summary>
    public void Advance(float dt, float truthRadius, float endRadius)
    {
        Age += dt;

        float limit = Remeshes ? truthRadius : float.PositiveInfinity;
        float advanced = MathF.Min(limit, DisplayRadius + WaveSpeed * dt);
        DisplayRadius = MathF.Max(DisplayRadius, advanced); // never back, even when the truth stalls

        if (!_finished && (DisplayRadius >= endRadius || Age >= MaxSeconds)) _finished = true;

        if (_finished)
        {
            _finishedFor += dt;
            Strength = MathF.Max(0f, 1f - _finishedFor / FadeOutSeconds);
            if (Strength <= 0f) Done = true;
        }
        else
        {
            Strength = MathF.Min(1f, Age / FadeInSeconds);
        }
    }
}
