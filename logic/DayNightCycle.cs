using Raylib_cs;
using System.Numerics;

namespace Terraformer.Rendering;

public class DayNightCycle
{
    // --- Settings ---
    public Vector3 Center = new Vector3(8, 8, 8);
    public float OrbitRadius = 60f;

    // Basislänge eines Tages (bei TimeScale = 1)
    public float DayLengthSeconds = 240f; // 4 Minuten

    // Speed Control
    public float TimeScale { get; private set; } = 1f;
    public float MinTimeScale = 0.10f;
    public float MaxTimeScale = 20.0f;
    public float TimeScaleStep = 1.25f; // multiplikativ

    public bool DrawSunAndMoon = true;

    // --- Outputs ---
    public Vector3 SunPosition { get; private set; }
    public Vector3 MoonPosition { get; private set; }

    /// <summary>0..1: 0 = Nacht, 1 = Tag</summary>
    public float Daylight01 { get; private set; }

    /// <summary>Background-Farbe für ClearBackground()</summary>
    public Color SkyColor { get; private set; }

    public string SpeedLabel => $"TimeScale: {TimeScale:0.00}x";

    private float _timeSeconds;

    public void Update(float dt)
    {
        // Keys: Z langsamer, U schneller
        if (Raylib.IsKeyPressed(KeyboardKey.U))
            TimeScale = Math.Clamp(TimeScale * TimeScaleStep, MinTimeScale, MaxTimeScale);

        if (Raylib.IsKeyPressed(KeyboardKey.Z))
            TimeScale = Math.Clamp(TimeScale / TimeScaleStep, MinTimeScale, MaxTimeScale);

        _timeSeconds += dt * TimeScale;

        // 0..1 Tagesphase
        float dayT = (_timeSeconds / Math.Max(1e-3f, DayLengthSeconds)) % 1f;
        float angle = dayT * MathF.Tau;

        // Sonne: Bahn
        SunPosition = Center + new Vector3(
            MathF.Cos(angle) * OrbitRadius,
            MathF.Sin(angle) * (OrbitRadius * 0.6f) + 10f,
            MathF.Sin(angle * 0.7f) * OrbitRadius
        );

        // Mond gegenüber
        MoonPosition = Center - (SunPosition - Center);

        // Daylight aus Sonnenhöhe, weich
        float sunHeight01 = Math.Clamp((SunPosition.Y - 2f) / 40f, 0f, 1f);
        float eased = SmoothStep(0.02f, 0.25f, sunHeight01);
        Daylight01 = MathF.Pow(eased, 1.2f);

        // SkyColor (stabil, kein Overlay!)
        Color night = new Color { R = 12, G = 16, B = 35, A = 255 };
        Color day = new Color { R = 135, G = 206, B = 235, A = 255 };

        // Dämmerung: warmes “Tint” nahe Sonnenauf/untergang
        float dusk = 1f - MathF.Abs(Daylight01 * 2f - 1f);
        dusk = Math.Clamp(dusk, 0f, 1f);

        Color baseSky = LerpColor(night, day, Daylight01);
        Color duskTint = new Color { R = 255, G = 150, B = 80, A = 255 };

        SkyColor = LerpColor(baseSky, duskTint, dusk * 0.25f);
    }

    /// <summary>In BeginMode3D() aufrufen</summary>
    public void Draw3D(Camera3D camera)
    {
        if (!DrawSunAndMoon) return;

        // Tagsüber Sonne sichtbar, nachts Mond stärker (optional)
        float night01 = 1f - Daylight01;

        Color sunColor = new Color { R = 255, G = 245, B = 200, A = 255 };
        Color moonColor = new Color { R = 180, G = 190, B = 210, A = 255 };

        // Sonne/Mond zeichnen (rein optisch)
        Raylib.DrawSphere(SunPosition, 2.0f, sunColor);

        // Mond nachts etwas “heller”: (Raylib Color hat kein Multiplizieren, also leicht anders)
        byte m = (byte)Math.Clamp(160 + (int)(80 * night01), 0, 255);
        Raylib.DrawSphere(MoonPosition, 1.6f, new Color { R = m, G = m, B = (byte)(m + 10 > 255 ? 255 : m + 10), A = 255 });
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
