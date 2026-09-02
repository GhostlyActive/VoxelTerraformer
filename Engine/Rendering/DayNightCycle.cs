using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

public class DayNightCycle
{
    // --- Settings ---
    public Vector3 Center = new Vector3(8, 8, 8);
    public float OrbitRadius = 60f;

    // Base length of one day (at TimeScale = 1)
    public float DayLengthSeconds = 240f; // 4 Minuten

    // Speed control
    public float TimeScale { get; set; } = 1f;
    public float MinTimeScale = 0.10f;
    public float MaxTimeScale = 20.0f;
    public float TimeScaleStep = 1.25f; // multiplicative

    public bool DrawSunAndMoon = true;

    /// <summary>
    /// Does time move on its own? When off, the sun stays wherever
    /// <see cref="SunAngleDegrees"/> puts it, and Z/U turn the sun instead of the clock.
    /// </summary>
    public bool AutoAdvance { get; set; } = true;

    /// <summary>Degrees per second that Z/U move the sun while <see cref="AutoAdvance"/> is off</summary>
    public float SunNudgeSpeed = 40f;

    // --- Outputs ---
    public Vector3 SunPosition { get; private set; }
    public Vector3 MoonPosition { get; private set; }

    /// <summary>0..1: 0 = night, 1 = day</summary>
    public float Daylight01 { get; private set; }

    /// <summary>Sky colour at the horizon, which is also the fog colour</summary>
    public Color SkyColor { get; private set; }

    /// <summary>Sky colour at the zenith, darker than the horizon, for the vertical gradient</summary>
    public Color SkyZenithColor { get; private set; }

    /// <summary>Normalized, points from the sun into the world (for the terrain shader)</summary>
    public Vector3 SunDirection { get; private set; } = new(0, -1, 0);

    /// <summary>Direct sunlight (0..1 per channel), warm at dusk, off at night</summary>
    public Vector3 SunlightColor { get; private set; }

    /// <summary>Ambient level (0..1 per channel), bluish at night</summary>
    public Vector3 AmbientColor { get; private set; }

    /// <summary>One line for the HUD: clock speed while time runs, otherwise where the sun stands</summary>
    public string SunLabel => AutoAdvance ? $"TimeScale: {TimeScale:0.00}x" : $"Sun: {SunAngleDegrees:0}°";

    private float _timeSeconds;

    /// <summary>
    /// Where the sun sits on its arc, in degrees: 0 = rising in the east, 90 = highest point,
    /// 180 = setting, 270 = lowest point below the horizon.
    /// </summary>
    public float SunAngleDegrees
    {
        get => Wrap360(_timeSeconds / MathF.Max(1e-3f, DayLengthSeconds) * 360f);
        set => _timeSeconds = Wrap360(value) / 360f * DayLengthSeconds;
    }

    /// <summary>Current time of day, for saving and loading</summary>
    public float TimeSeconds
    {
        get => _timeSeconds;
        set => _timeSeconds = value;
    }

    private static float Wrap360(float degrees)
    {
        degrees %= 360f;
        return degrees < 0f ? degrees + 360f : degrees;
    }

    public void Update(float dt)
    {
        if (AutoAdvance)
        {
            // Keys: Z slower, U faster
            if (Raylib.IsKeyPressed(KeyboardKey.U))
                TimeScale = Math.Clamp(TimeScale * TimeScaleStep, MinTimeScale, MaxTimeScale);

            if (Raylib.IsKeyPressed(KeyboardKey.Z))
                TimeScale = Math.Clamp(TimeScale / TimeScaleStep, MinTimeScale, MaxTimeScale);

            _timeSeconds += dt * TimeScale;
        }
        else
        {
            // With the clock stopped the same keys become a sun dial
            if (Raylib.IsKeyDown(KeyboardKey.U)) SunAngleDegrees += SunNudgeSpeed * dt;
            if (Raylib.IsKeyDown(KeyboardKey.Z)) SunAngleDegrees -= SunNudgeSpeed * dt;
        }

        // 0..1 Tagesphase
        float dayT = (_timeSeconds / Math.Max(1e-3f, DayLengthSeconds)) % 1f;
        float angle = dayT * MathF.Tau;

        // Sun: its arc
        SunPosition = Center + new Vector3(
            MathF.Cos(angle) * OrbitRadius,
            MathF.Sin(angle) * (OrbitRadius * 0.6f) + 10f,
            MathF.Sin(angle * 0.7f) * OrbitRadius
        );

        // Moon on the opposite side
        MoonPosition = Center - (SunPosition - Center);

        // Daylight from the sun's height, smoothed
        float sunHeight01 = Math.Clamp((SunPosition.Y - 2f) / 40f, 0f, 1f);
        float eased = SmoothStep(0.02f, 0.25f, sunHeight01);
        Daylight01 = MathF.Pow(eased, 1.2f);

        // SkyColor (stable, no overlay)
        Color night = new Color { R = 12, G = 16, B = 35, A = 255 };
        Color day = new Color { R = 135, G = 206, B = 235, A = 255 };

        // Dusk: a warm tint near sunrise and sunset
        float dusk = 1f - MathF.Abs(Daylight01 * 2f - 1f);
        dusk = Math.Clamp(dusk, 0f, 1f);

        Color baseSky = LerpColor(night, day, Daylight01);
        Color duskTint = new Color { R = 255, G = 150, B = 80, A = 255 };

        SkyColor = LerpColor(baseSky, duskTint, dusk * 0.25f);

        // The zenith sits clearly darker than the horizon; dusk only tints it lightly
        Color nightZenith = new Color { R = 4, G = 6, B = 18, A = 255 };
        Color dayZenith = new Color { R = 70, G = 130, B = 215, A = 255 };
        SkyZenithColor = LerpColor(LerpColor(nightZenith, dayZenith, Daylight01), duskTint, dusk * 0.10f);

        // Light for the terrain shader: neutral-warm at noon, orange at dusk, ambient only at night
        SunDirection = Vector3.Normalize(Center - SunPosition);

        // Tie the warmth of the light to the real sun elevation (low = orange), not to the daylight value
        float sunElevation = MathF.Max(0f, -SunDirection.Y);
        float lowSun = 1f - SmoothStep(0.08f, 0.50f, sunElevation);

        Vector3 noonColor = new(1.00f, 0.97f, 0.90f);
        Vector3 duskColor = new(1.00f, 0.55f, 0.25f);
        SunlightColor = Vector3.Lerp(noonColor, duskColor, lowSun * 0.85f) * (0.75f * Daylight01);

        Vector3 nightAmbient = new(0.20f, 0.22f, 0.32f);
        Vector3 dayAmbient = new(0.42f, 0.44f, 0.47f);
        AmbientColor = Vector3.Lerp(nightAmbient, dayAmbient, Daylight01);
    }

    /// <summary>Call inside BeginMode3D()</summary>
    public void Draw3D(Camera3D camera)
    {
        if (!DrawSunAndMoon) return;

        // Sun visible by day, moon stronger at night
        float night01 = 1f - Daylight01;

        Color sunColor = new Color { R = 255, G = 245, B = 200, A = 255 };
        Color moonColor = new Color { R = 180, G = 190, B = 210, A = 255 };

        // Draw sun and moon (purely cosmetic); their size scales with the orbit radius
        float sunRadius = OrbitRadius * 0.035f;
        Raylib.DrawSphere(SunPosition, sunRadius, sunColor);

        // The moon reads a little brighter at night (raylib Color has no multiply, hence the detour)
        byte m = (byte)Math.Clamp(160 + (int)(80 * night01), 0, 255);
        Raylib.DrawSphere(MoonPosition, sunRadius * 0.8f, new Color { R = m, G = m, B = (byte)(m + 10 > 255 ? 255 : m + 10), A = 255 });
    }

    private static float SmoothStep(float a, float b, float t)
    {
        t = Math.Clamp((t - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color
        {
            R = (byte)(a.R + (b.R - a.R) * t),
            G = (byte)(a.G + (b.G - a.G) * t),
            B = (byte)(a.B + (b.B - a.B) * t),
            A = 255
        };
    }
}
