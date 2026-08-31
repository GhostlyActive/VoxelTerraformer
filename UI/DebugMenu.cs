using Raylib_cs;
using Terraformer.Config;

namespace Terraformer.UI;

/// <summary>
/// Debug-Tuning-Menü auf Taste M: Pfeiltasten navigieren und ändern Werte live,
/// R setzt auf Defaults zurück, Schließen speichert. Das Spiel läuft dabei weiter,
/// damit sich Änderungen sofort erfühlen lassen.
/// </summary>
public sealed class DebugMenu
{
    private sealed record Entry(
        string Label,
        Func<float> Get,
        Action<float> Set,
        float Step,
        float Min,
        float Max,
        string Format);

    private readonly DebugSettings _settings;
    private readonly Entry[] _entries;
    private int _selected;

    public bool IsOpen { get; private set; }

    public DebugMenu(DebugSettings settings)
    {
        _settings = settings;
        _entries = new[]
        {
            new Entry("Maus-Empfindlichkeit", () => settings.MouseSensitivity, v => settings.MouseSensitivity = v, 0.01f, 0.02f, 0.50f, "0.00"),
            new Entry("Laufgeschwindigkeit", () => settings.WalkSpeed, v => settings.WalkSpeed = v, 0.5f, 1f, 30f, "0.0"),
            new Entry("Sprint-Faktor", () => settings.SprintMultiplier, v => settings.SprintMultiplier = v, 0.1f, 1f, 4f, "0.0"),
            new Entry("Sprungkraft", () => settings.JumpSpeed, v => settings.JumpSpeed = v, 0.5f, 2f, 30f, "0.0"),
            new Entry("Gravitation", () => settings.Gravity, v => settings.Gravity = v, 1f, 2f, 60f, "0"),
            new Entry("Plattform-Dauer (s)", () => settings.PlatformLifetime, v => settings.PlatformLifetime = v, 0.5f, 0.5f, 30f, "0.0"),
            new Entry("Plattform-Ladungen", () => settings.PlatformCharges, v => settings.PlatformCharges = (int)MathF.Round(v), 1f, 1f, 9f, "0"),
        };
    }

    public void Update()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.M))
        {
            IsOpen = !IsOpen;
            if (!IsOpen) _settings.Save();
        }

        if (!IsOpen) return;

        if (Pressed(KeyboardKey.Down)) _selected = (_selected + 1) % _entries.Length;
        if (Pressed(KeyboardKey.Up)) _selected = (_selected - 1 + _entries.Length) % _entries.Length;

        Entry entry = _entries[_selected];
        float direction = 0f;
        if (Pressed(KeyboardKey.Right)) direction = 1f;
        if (Pressed(KeyboardKey.Left)) direction = -1f;
        if (direction != 0f)
            entry.Set(Math.Clamp(entry.Get() + direction * entry.Step, entry.Min, entry.Max));

        if (Raylib.IsKeyPressed(KeyboardKey.R))
            _settings.ResetToDefaults();
    }

    // Gedrückt halten wiederholt die Eingabe (Key-Repeat des Systems)
    private static bool Pressed(KeyboardKey key)
        => Raylib.IsKeyPressed(key) || Raylib.IsKeyPressedRepeat(key);

    public void Draw(int screenWidth)
    {
        if (!IsOpen) return;

        const int width = 400;
        const int rowHeight = 26;
        int x = screenWidth - width - 16;
        int y = 40;
        int height = 66 + _entries.Length * rowHeight;

        Raylib.DrawRectangle(x, y, width, height, new Color(10, 14, 22, 205));
        Raylib.DrawRectangleLines(x, y, width, height, new Color(95, 225, 235, 200));

        Raylib.DrawText("DEBUG-TUNING", x + 12, y + 10, 20, new Color(95, 225, 235, 255));
        Raylib.DrawText("Pfeile: navigieren + einstellen | R: Reset | M: speichern", x + 12, y + 34, 14, new Color(180, 190, 200, 255));

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            int rowY = y + 58 + i * rowHeight;
            bool selected = i == _selected;

            if (selected)
                Raylib.DrawRectangle(x + 6, rowY - 3, width - 12, rowHeight - 2, new Color(95, 225, 235, 40));

            Color textColor = selected ? new Color(210, 250, 255, 255) : new Color(200, 205, 215, 255);
            Raylib.DrawText(entry.Label, x + 16, rowY, 20, textColor);

            string value = entry.Get().ToString(entry.Format);
            int valueWidth = Raylib.MeasureText(value, 20);
            Raylib.DrawText(value, x + width - 20 - valueWidth, rowY, 20, textColor);
        }
    }
}
