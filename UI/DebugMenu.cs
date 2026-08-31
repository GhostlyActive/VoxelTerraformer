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
        float Default,
        float Step,
        float Min,
        float Max,
        string Format);

    private readonly DebugSettings _settings;
    private readonly Entry[] _entries;
    private int _selected;

    public bool IsOpen { get; private set; }

    // Letzte Zeile im Menü ist der "Reset all"-Eintrag
    private int ResetAllRow => _entries.Length;

    public DebugMenu(DebugSettings settings)
    {
        _settings = settings;
        var defaults = new DebugSettings();
        _entries = new[]
        {
            new Entry("Mouse sensitivity", () => settings.MouseSensitivity, v => settings.MouseSensitivity = v, defaults.MouseSensitivity, 0.01f, 0.02f, 0.50f, "0.00"),
            new Entry("Walk speed", () => settings.WalkSpeed, v => settings.WalkSpeed = v, defaults.WalkSpeed, 0.5f, 1f, 30f, "0.0"),
            new Entry("Sprint multiplier", () => settings.SprintMultiplier, v => settings.SprintMultiplier = v, defaults.SprintMultiplier, 0.1f, 1f, 4f, "0.0"),
            new Entry("Jump power", () => settings.JumpSpeed, v => settings.JumpSpeed = v, defaults.JumpSpeed, 0.5f, 2f, 30f, "0.0"),
            new Entry("Gravity", () => settings.Gravity, v => settings.Gravity = v, defaults.Gravity, 1f, 2f, 60f, "0"),
            new Entry("Build reach", () => settings.BuildReach, v => settings.BuildReach = v, defaults.BuildReach, 1f, 2f, 60f, "0"),
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

        int rowCount = _entries.Length + 1; // + "Reset all"-Zeile
        if (Pressed(KeyboardKey.Down)) _selected = (_selected + 1) % rowCount;
        if (Pressed(KeyboardKey.Up)) _selected = (_selected - 1 + rowCount) % rowCount;

        if (_selected == ResetAllRow)
        {
            bool trigger =
                Raylib.IsKeyPressed(KeyboardKey.Enter) ||
                Pressed(KeyboardKey.Left) ||
                Pressed(KeyboardKey.Right) ||
                Raylib.IsKeyPressed(KeyboardKey.R);
            if (trigger) _settings.ResetToDefaults();
            return;
        }

        Entry entry = _entries[_selected];
        float direction = 0f;
        if (Pressed(KeyboardKey.Right)) direction = 1f;
        if (Pressed(KeyboardKey.Left)) direction = -1f;
        if (direction != 0f)
            entry.Set(Math.Clamp(entry.Get() + direction * entry.Step, entry.Min, entry.Max));

        // R setzt nur den ausgewählten Wert zurück; für alles gibt es die "Reset all"-Zeile
        if (Raylib.IsKeyPressed(KeyboardKey.R))
            entry.Set(entry.Default);
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
        int height = 66 + (_entries.Length + 1) * rowHeight;

        Raylib.DrawRectangle(x, y, width, height, new Color(10, 14, 22, 205));
        Raylib.DrawRectangleLines(x, y, width, height, new Color(95, 225, 235, 200));

        Raylib.DrawText("DEBUG TUNING", x + 12, y + 10, 20, new Color(95, 225, 235, 255));
        Raylib.DrawText("Arrows: navigate + adjust | R: reset value | M: save", x + 12, y + 34, 14, new Color(180, 190, 200, 255));

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

        // "Reset all"-Zeile
        int resetRowY = y + 58 + _entries.Length * rowHeight;
        bool resetSelected = _selected == ResetAllRow;

        if (resetSelected)
            Raylib.DrawRectangle(x + 6, resetRowY - 3, width - 12, rowHeight - 2, new Color(255, 150, 90, 45));

        Color resetColor = resetSelected ? new Color(255, 190, 140, 255) : new Color(220, 160, 120, 255);
        Raylib.DrawText("Reset ALL to defaults (Enter)", x + 16, resetRowY, 20, resetColor);
    }
}
