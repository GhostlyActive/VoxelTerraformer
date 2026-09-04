using Raylib_cs;
using VoxelEngine.Config;

namespace VoxelEngine.UI;

/// <summary>
/// The tuning menu on key M: the arrow keys navigate and change values live, R restores the
/// defaults, closing saves. The game keeps running underneath so a change can be felt right away.
/// Entries are grouped into sections; headings cannot be selected.
/// </summary>
public sealed class TuningMenu
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

    // A row is either a section heading or a value; Header == null means value.
    private sealed record Row(string? Header, Entry? Entry);

    private readonly EngineSettings _settings;
    private readonly Row[] _rows;
    private readonly int[] _selectableRows;
    private int _selected;          // Index in _selectableRows
    private float _resetFlashTimer;
    private float _scroll;

    public bool IsOpen { get; private set; }

    // The "Reset all" row sits behind every section
    private int ResetAllRow => _rows.Length;

    public TuningMenu(EngineSettings settings)
    {
        _settings = settings;
        var defaults = new EngineSettings();

        _rows = new[]
        {
            Section("LOOK & MOVE"),
            Value("Mouse sensitivity", () => settings.MouseSensitivity, v => settings.MouseSensitivity = v, defaults.MouseSensitivity, 0.01f, 0.02f, 0.50f, "0.00"),
            Value("Field of view", () => settings.FieldOfView, v => settings.FieldOfView = v, defaults.FieldOfView, 2f, 50f, 110f, "0"),
            Value("Walk speed", () => settings.WalkSpeed, v => settings.WalkSpeed = v, defaults.WalkSpeed, 0.5f, 1f, 30f, "0.0"),
            Value("Sprint multiplier", () => settings.SprintMultiplier, v => settings.SprintMultiplier = v, defaults.SprintMultiplier, 0.1f, 1f, 4f, "0.0"),
            Value("Jump power", () => settings.JumpSpeed, v => settings.JumpSpeed = v, defaults.JumpSpeed, 0.5f, 2f, 30f, "0.0"),
            Value("Gravity", () => settings.Gravity, v => settings.Gravity = v, defaults.Gravity, 1f, 2f, 60f, "0"),

            Section("TOOL"),
            Value("Build reach", () => settings.BuildReach, v => settings.BuildReach = v, defaults.BuildReach, 1f, 2f, 60f, "0"),
            Value("Sculpt radius", () => settings.SculptRadius, v => settings.SculptRadius = v, defaults.SculptRadius, 0.1f, 0.25f, 2.5f, "0.0"),
            Value("Brush softness", () => settings.BrushSoftness, v => settings.BrushSoftness = v, defaults.BrushSoftness, 0.1f, 0f, 1.6f, "0.0"),
            Value("Preview hold s", () => settings.PreviewHold, v => settings.PreviewHold = v, defaults.PreviewHold, 0.25f, 0f, 5f, "0.00"),

            Section("WORLD"),
            Value("Sun angle deg", () => settings.SunAngle, v => settings.SunAngle = v, defaults.SunAngle, 5f, 0f, 360f, "0"),
            Value("Time flow", () => settings.TimeFlow, v => settings.TimeFlow = v, defaults.TimeFlow, 0.25f, 0f, 20f, "0.00"),
            Value("Day length s", () => settings.DayLengthSeconds, v => settings.DayLengthSeconds = v, defaults.DayLengthSeconds, 15f, 30f, 1800f, "0"),
            Value("Fog start", () => settings.FogStart, v => settings.FogStart = v, defaults.FogStart, 20f, 0f, 1400f, "0"),
            Value("Fog end", () => settings.FogEnd, v => settings.FogEnd = v, defaults.FogEnd, 20f, 40f, 1500f, "0"),

            Section("VIEW"),
            Value("View distance chunks", () => settings.ViewDistanceChunks, v => settings.ViewDistanceChunks = (int)v, defaults.ViewDistanceChunks, 2f, 6f, World.VoxelWorld.MaxViewDistance, "0"),
            Value("Detail radius chunks", () => settings.DetailRadiusChunks, v => settings.DetailRadiusChunks = (int)v, defaults.DetailRadiusChunks, 1f, 2f, World.VoxelWorld.MaxViewDistance, "0"),
            Value("Sculpt grid", () => settings.SculptGridStrength, v => settings.SculptGridStrength = v, defaults.SculptGridStrength, 0.25f, 0f, 1f, "0.00"),
            Value("Brush always visible", () => settings.ShowBrushAlways ? 1f : 0f, v => settings.ShowBrushAlways = v >= 0.5f, defaults.ShowBrushAlways ? 1f : 0f, 1f, 0f, 1f, "0"),

            Section("CLOUDS"),
            Value("Coverage", () => settings.CloudCoverage, v => settings.CloudCoverage = v, defaults.CloudCoverage, 0.05f, 0f, 1f, "0.00"),
            Value("Height", () => settings.CloudHeight, v => settings.CloudHeight = v, defaults.CloudHeight, 5f, 40f, 200f, "0"),
            Value("Drift speed", () => settings.CloudDrift, v => settings.CloudDrift = v, defaults.CloudDrift, 0.2f, 0f, 12f, "0.0"),
        };

        var selectable = new List<int>();
        for (int i = 0; i < _rows.Length; i++)
            if (_rows[i].Entry != null) selectable.Add(i);
        selectable.Add(ResetAllRow);
        _selectableRows = selectable.ToArray();
    }

    private static Row Section(string header) => new(header, null);

    private static Row Value(
        string label, Func<float> get, Action<float> set,
        float defaultValue, float step, float min, float max, string format)
        => new(null, new Entry(label, get, set, defaultValue, step, min, max, format));

    public void Update()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.M))
        {
            IsOpen = !IsOpen;
            if (!IsOpen) _settings.Save();
        }

        if (!IsOpen) return;

        if (Pressed(KeyboardKey.Down)) _selected = (_selected + 1) % _selectableRows.Length;
        if (Pressed(KeyboardKey.Up)) _selected = (_selected - 1 + _selectableRows.Length) % _selectableRows.Length;

        _resetFlashTimer = Math.Max(0f, _resetFlashTimer - Raylib.GetFrameTime());

        if (_selectableRows[_selected] == ResetAllRow)
        {
            // Depending on the keyboard, Enter may arrive as numpad Enter
            bool trigger =
                Raylib.IsKeyPressed(KeyboardKey.Enter) ||
                Raylib.IsKeyPressed(KeyboardKey.KpEnter) ||
                Pressed(KeyboardKey.Left) ||
                Pressed(KeyboardKey.Right) ||
                Raylib.IsKeyPressed(KeyboardKey.R);
            if (trigger) ResetAll();
            return;
        }

        Entry entry = _rows[_selectableRows[_selected]].Entry!;
        float direction = 0f;
        if (Pressed(KeyboardKey.Right)) direction = 1f;
        if (Pressed(KeyboardKey.Left)) direction = -1f;
        if (direction != 0f)
            entry.Set(Math.Clamp(entry.Get() + direction * entry.Step, entry.Min, entry.Max));

        // R only resets the selected value; the "Reset all" row covers everything
        if (Raylib.IsKeyPressed(KeyboardKey.R))
            entry.Set(entry.Default);
    }

    // Reset through the entries themselves, which guarantees everything in the menu is covered
    private void ResetAll()
    {
        foreach (Row row in _rows)
            row.Entry?.Set(row.Entry.Default);
        _resetFlashTimer = 1.5f;
    }

    // Holding a key repeats the input (the system's key repeat)
    private static bool Pressed(KeyboardKey key)
        => Raylib.IsKeyPressed(key) || Raylib.IsKeyPressedRepeat(key);

    public void Draw(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;

        const int width = 400;
        const int rowHeight = 24;
        const int headerHeight = 30;
        const int headerY = 40;
        const int bodyTop = 58;

        int x = screenWidth - width - 16;
        int contentHeight = 0;
        foreach (Row row in _rows) contentHeight += row.Header != null ? headerHeight : rowHeight;
        contentHeight += rowHeight; // "Reset all"

        int maxHeight = screenHeight - headerY - 24;
        int height = Math.Min(bodyTop + contentHeight + 10, maxHeight);
        int viewHeight = height - bodyTop - 10;

        // Keep the selection in view when the list is longer than the window
        _scroll = Math.Clamp(_scroll, RowOffset(_selected) + rowHeight - viewHeight, RowOffset(_selected));
        _scroll = Math.Clamp(_scroll, 0f, Math.Max(0f, contentHeight - viewHeight));

        Raylib.DrawRectangle(x, headerY, width, height, new Color(10, 14, 22, 214));
        Raylib.DrawRectangleLines(x, headerY, width, height, new Color(95, 225, 235, 200));

        Raylib.DrawText("DEBUG TUNING", x + 12, headerY + 10, 20, new Color(95, 225, 235, 255));
        Raylib.DrawText("Arrows: navigate + adjust | R: reset | M: save", x + 12, headerY + 34, 14, new Color(180, 190, 200, 255));

        Raylib.BeginScissorMode(x + 1, headerY + bodyTop, width - 2, viewHeight);

        int offset = 0;
        for (int i = 0; i < _rows.Length; i++)
        {
            Row row = _rows[i];
            int rowY = headerY + bodyTop + offset - (int)_scroll;

            if (row.Header != null)
            {
                Raylib.DrawText(row.Header, x + 14, rowY + 8, 14, new Color(120, 200, 215, 220));
                Raylib.DrawLine(x + 14, rowY + 25, x + width - 14, rowY + 25, new Color(95, 225, 235, 60));
                offset += headerHeight;
                continue;
            }

            DrawValueRow(row.Entry!, x, rowY, width, rowHeight, _selectableRows[_selected] == i);
            offset += rowHeight;
        }

        DrawResetRow(x, headerY + bodyTop + offset - (int)_scroll, width, rowHeight);

        Raylib.EndScissorMode();
    }

    private void DrawValueRow(Entry entry, int x, int rowY, int width, int rowHeight, bool selected)
    {
        if (selected)
            Raylib.DrawRectangle(x + 6, rowY - 2, width - 12, rowHeight - 2, new Color(95, 225, 235, 40));

        Color textColor = selected ? new Color(210, 250, 255, 255) : new Color(200, 205, 215, 255);
        Raylib.DrawText(entry.Label, x + 16, rowY, 18, textColor);

        string value = entry.Get().ToString(entry.Format);
        int valueWidth = Raylib.MeasureText(value, 18);
        Raylib.DrawText(value, x + width - 20 - valueWidth, rowY, 18, textColor);
    }

    private void DrawResetRow(int x, int rowY, int width, int rowHeight)
    {
        bool selected = _selectableRows[_selected] == ResetAllRow;

        if (selected)
            Raylib.DrawRectangle(x + 6, rowY - 2, width - 12, rowHeight - 2, new Color(255, 150, 90, 45));

        if (_resetFlashTimer > 0f)
        {
            Raylib.DrawText("All values reset!", x + 16, rowY, 18, new Color(140, 240, 160, 255));
            return;
        }

        Color color = selected ? new Color(255, 190, 140, 255) : new Color(220, 160, 120, 255);
        Raylib.DrawText("Reset ALL to defaults (Enter/R)", x + 16, rowY, 18, color);
    }

    // Pixel position of a selectable row inside the content, for scrolling along
    private float RowOffset(int selectableIndex)
    {
        const int rowHeight = 24;
        const int headerHeight = 30;

        int target = _selectableRows[selectableIndex];
        int offset = 0;

        for (int i = 0; i < _rows.Length && i < target; i++)
            offset += _rows[i].Header != null ? headerHeight : rowHeight;

        return offset;
    }
}
