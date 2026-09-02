using Raylib_cs;
using System.Numerics;

namespace Terraformer.Games.SolarSystem;

/// <summary>
/// The blast front of a dying planet: a glowing shell racing outward, and a flat ring of shards
/// thrown out along its equator.
///
/// The rubble does the physical work — this is the part that reads as a shockwave, because it
/// outruns the debris by a wide margin and then thins out. Both start fast and slow as they grow,
/// the way a blast loses its punch, and both fade to nothing rather than being switched off.
/// </summary>
public sealed class ShockwaveField
{
    // Short and close on purpose. Reaching much further means the camera ends up inside the front,
    // and a blast seen from within is just a haze over the whole screen.
    private const float ShellSeconds = 0.9f;
    private const float RingSeconds = 2.0f;

    /// <summary>How far shell and ring travel, in radii of the body that produced them</summary>
    private const float ShellReach = 2.2f;
    private const float RingReach = 5f;

    private const int RingShards = 72;

    /// <summary>Radius factor and share of the opacity per shell of the flash</summary>
    private static readonly (float Scale, float Alpha)[] _flashShells =
    {
        (0.55f, 1f), (0.80f, 0.55f), (1f, 0.28f),
    };

    private sealed class Wave
    {
        public Vector3 Center;
        public Vector3 Drift;
        public float BodyRadius;
        public float Age;
    }

    private readonly List<Wave> _waves = new();

    public int Count => _waves.Count;

    /// <summary><paramref name="drift"/> is the dead body's orbital motion, which the front keeps</summary>
    public void Spawn(Vector3 center, Vector3 drift, float bodyRadius)
        => _waves.Add(new Wave { Center = center, Drift = drift, BodyRadius = bodyRadius });

    public void Clear() => _waves.Clear();

    public void Update(float dt)
    {
        for (int i = _waves.Count - 1; i >= 0; i--)
        {
            Wave wave = _waves[i];

            wave.Age += dt;
            wave.Center += wave.Drift * dt;

            if (wave.Age > RingSeconds) _waves.RemoveAt(i);
        }
    }

    public void Draw()
    {
        Raylib.BeginBlendMode(BlendMode.Additive);

        foreach (Wave wave in _waves)
        {
            DrawShell(wave);
            DrawRing(wave);
        }

        Raylib.EndBlendMode();
    }

    /// <summary>
    /// The spherical front: a flash that swells and is gone inside a second. A wire sphere was the
    /// obvious choice and the wrong one — held long enough to see, it turns into a net draped over
    /// the entire view the moment the camera is inside it.
    /// </summary>
    private static void DrawShell(Wave wave)
    {
        float life = wave.Age / ShellSeconds;
        if (life >= 1f) return;

        float radius = wave.BodyRadius * (0.3f + EaseOut(life) * ShellReach);
        float fade = Brightness(life);

        // Nested shells rather than one sphere: a single additive ball has a hard rim and reads as
        // a disc pasted behind the ring, where a stack falls off towards its edge
        foreach ((float scale, float alpha) in _flashShells)
            Raylib.DrawSphereEx(wave.Center, radius * scale, 16, 16, new Color(
                (byte)255,
                (byte)(245 - 70 * life),
                (byte)(210 - 90 * life),
                (byte)(190 * fade * alpha)));
    }

    /// <summary>
    /// The flat ring along the equator: a band of shards rather than a smooth disc, which is both
    /// closer to the rest of the look and cheap — 72 boxes, no geometry to build.
    /// </summary>
    private static void DrawRing(Wave wave)
    {
        float life = wave.Age / RingSeconds;
        if (life >= 1f) return;

        float radius = wave.BodyRadius * (0.5f + EaseOut(life) * RingReach);
        float thickness = wave.BodyRadius * 0.5f * (1f - life * 0.7f);
        float shardLength = MathF.Tau * radius / RingShards * 0.85f;
        float fade = Brightness(life);

        var color = new Color(
            (byte)255,
            (byte)(235 - 90 * life),
            (byte)(180 - 110 * life),
            (byte)(255 * fade));

        for (int i = 0; i < RingShards; i++)
        {
            float angle = i / (float)RingShards * MathF.Tau;
            Vector3 position = wave.Center + new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * radius;

            Rlgl.PushMatrix();
            Rlgl.Translatef(position.X, position.Y, position.Z);

            // Turn the box so its long side lies along the ring instead of pointing outwards
            Rlgl.Rotatef(angle * (180f / MathF.PI) + 90f, 0f, 1f, 0f);

            Raylib.DrawCubeV(Vector3.Zero, new Vector3(shardLength, thickness * 0.45f, thickness), color);

            Rlgl.PopMatrix();
        }
    }

    /// <summary>Fast off the mark, slowing as it spreads</summary>
    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    /// <summary>
    /// Additive blending multiplies the colour by its own alpha, so a front that fades linearly
    /// spends most of its life as a dull brown smear. Holding full brightness through the first
    /// half and dropping off late is what makes it read as white heat.
    /// </summary>
    private static float Brightness(float life) => 1f - life * life * MathF.Sqrt(life);
}
