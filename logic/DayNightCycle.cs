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
    public float TimeScale { get; set; } = 1f;
    public float MinTimeScale = 0.10f;
    public float MaxTimeScale = 20.0f;
    public float TimeScaleStep = 1.25f; // multiplikativ

    public bool DrawSunAndMoon = true;

    // --- Outputs ---
    public Vector3 SunPosition { get; private set; }
    public Vector3 MoonPosition { get; private set; }

    /// <summary>0..1: 0 = Nacht, 1 = Tag</summary>
    public float Daylight01 { get; private set; }

    /// <summary>Himmelsfarbe am Horizont (auch Fog-Farbe)</summary>
    public Color SkyColor { get; private set; }

    /// <summary>Himmelsfarbe am Zenit — dunkler als der Horizont, für den vertikalen Verlauf</summary>
    public Color SkyZenithColor { get; private set; }

    /// <summary>Normiert, zeigt von der Sonne in die Welt (für den Terrain-Shader)</summary>
    public Vector3 SunDirection { get; private set; } = new(0, -1, 0);

    /// <summary>Direktes Sonnenlicht (0..1 pro Kanal), in der Dämmerung warm, nachts aus</summary>
    public Vector3 SunlightColor { get; private set; }

    /// <summary>Grundhelligkeit (0..1 pro Kanal), nachts bläulich</summary>
    public Vector3 AmbientColor { get; private set; }

    public string SpeedLabel => $"TimeScale: {TimeScale:0.00}x";

    private float _timeSeconds;

    /// <summary>Tageszeit vorspulen (z. B. für Tests oder einen definierten Spielstart)</summary>
    public void AdvanceTime(float seconds) => _timeSeconds += seconds;

    /// <summary>
    /// Tageszeit in Stunden, 12 = Sonnenhöchststand. Die Bahn startet bei Phase 0 am
    /// Osthorizont, deshalb der Versatz von 6 Stunden.
    /// </summary>
    public float TimeOfDayHours
    {
        get
        {
            float phase = (_timeSeconds / MathF.Max(1e-3f, DayLengthSeconds)) % 1f;
            if (phase < 0f) phase += 1f;
            return (phase * 24f + 6f) % 24f;
        }
        set
        {
            float phase = (((value - 6f) % 24f) + 24f) % 24f / 24f;
            _timeSeconds = phase * DayLengthSeconds;
        }
    }

    /// <summary>Aktuelle Tageszeit — für Speichern/Laden</summary>
    public float TimeSeconds
    {
        get => _timeSeconds;
        set => _timeSeconds = value;
    }

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

        // Zenit deutlich tiefer/dunkler als der Horizont, Dämmerung färbt ihn nur leicht
        Color nightZenith = new Color { R = 4, G = 6, B = 18, A = 255 };
        Color dayZenith = new Color { R = 70, G = 130, B = 215, A = 255 };
        SkyZenithColor = LerpColor(LerpColor(nightZenith, dayZenith, Daylight01), duskTint, dusk * 0.10f);

        // Licht fürs Terrain (Shader): mittags neutral-warm, in der Dämmerung orange, nachts nur Ambient
        SunDirection = Vector3.Normalize(Center - SunPosition);

        // Wärme des Lichts am echten Sonnenstand festmachen (tief = orange), nicht am Daylight-Wert
        float sunElevation = MathF.Max(0f, -SunDirection.Y);
        float lowSun = 1f - SmoothStep(0.08f, 0.50f, sunElevation);

        Vector3 noonColor = new(1.00f, 0.97f, 0.90f);
        Vector3 duskColor = new(1.00f, 0.55f, 0.25f);
        SunlightColor = Vector3.Lerp(noonColor, duskColor, lowSun * 0.85f) * (0.75f * Daylight01);

        Vector3 nightAmbient = new(0.20f, 0.22f, 0.32f);
        Vector3 dayAmbient = new(0.42f, 0.44f, 0.47f);
        AmbientColor = Vector3.Lerp(nightAmbient, dayAmbient, Daylight01);
    }

    /// <summary>In BeginMode3D() aufrufen</summary>
    public void Draw3D(Camera3D camera)
    {
        if (!DrawSunAndMoon) return;

        // Tagsüber Sonne sichtbar, nachts Mond stärker (optional)
        float night01 = 1f - Daylight01;

        Color sunColor = new Color { R = 255, G = 245, B = 200, A = 255 };
        Color moonColor = new Color { R = 180, G = 190, B = 210, A = 255 };

        // Sonne/Mond zeichnen (rein optisch) — Größe skaliert mit dem Orbit-Radius
        float sunRadius = OrbitRadius * 0.035f;
        Raylib.DrawSphere(SunPosition, sunRadius, sunColor);

        // Mond nachts etwas “heller”: (Raylib Color hat kein Multiplizieren, also leicht anders)
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
