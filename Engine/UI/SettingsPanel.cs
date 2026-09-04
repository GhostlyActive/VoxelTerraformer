using Raylib_cs;

namespace VoxelEngine.UI;

/// <summary>
/// A list of dial sections with one selection: the arrow keys walk the rows and change the
/// values, R restores the selected value, the last row restores everything. The tuning menu and
/// the settings page of the pause menu are both one of these in a frame of their own; headings
/// cannot be selected.
/// </summary>
public sealed class SettingsPanel
{
    public const int RowHeight = 24;
    private const int HeaderHeight = 30;

    private readonly List<TuningSection> _sections = new();

    private int _selected;
    private float _resetFlashTimer;
    private float _scroll;

    /// <summary>Adds a group of dials; <paramref name="onSave"/> runs on <see cref="SaveAll"/> and when the section is removed</summary>
    public TuningSection AddSection(string title, Action? onSave = null)
    {
        var section = new TuningSection(title, onSave);
        _sections.Add(section);
        return section;
    }

    /// <summary>Takes a section out again, saving it first</summary>
    public void RemoveSection(TuningSection section)
    {
        if (!_sections.Remove(section)) return;

        section.OnSave?.Invoke();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, SelectableCount - 1));
    }

    public void SaveAll()
    {
        foreach (TuningSection section in _sections)
            section.OnSave?.Invoke();
    }

    /// <summary>Height of every heading and row, for sizing the frame around the panel</summary>
    public int ContentHeight
    {
        get
        {
            int height = 0;
            foreach (TuningSection section in _sections)
                height += HeaderHeight + section.Entries.Count * RowHeight;

            return height + RowHeight; // "Reset all"
        }
    }

    private int SelectableCount => _sections.Sum(section => section.Entries.Count) + 1; // plus "Reset all"

    private bool IsResetRow(int selectable) => selectable == SelectableCount - 1;

    private TuningSection.Entry? EntryAt(int selectable)
    {
        foreach (TuningSection section in _sections)
        {
            if (selectable < section.Entries.Count) return section.Entries[selectable];
            selectable -= section.Entries.Count;
        }

        return null;
    }

    public void Update()
    {
        int count = SelectableCount;
        if (Pressed(KeyboardKey.Down)) _selected = (_selected + 1) % count;
        if (Pressed(KeyboardKey.Up)) _selected = (_selected - 1 + count) % count;

        _resetFlashTimer = Math.Max(0f, _resetFlashTimer - Raylib.GetFrameTime());

        if (IsResetRow(_selected))
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

        TuningSection.Entry? entry = EntryAt(_selected);
        if (entry == null) return;

        float direction = 0f;
        if (Pressed(KeyboardKey.Right)) direction = 1f;
        if (Pressed(KeyboardKey.Left)) direction = -1f;
        if (direction != 0f)
            entry.Set(Math.Clamp(entry.Get() + direction * entry.Step, entry.Min, entry.Max));

        // R only resets the selected value; the "Reset all" row covers everything
        if (Raylib.IsKeyPressed(KeyboardKey.R))
            entry.Set(entry.Default);
    }

    // Reset through the entries themselves, which guarantees everything in the panel is covered
    private void ResetAll()
    {
        foreach (TuningSection section in _sections)
        foreach (TuningSection.Entry entry in section.Entries)
            entry.Set(entry.Default);

        _resetFlashTimer = 1.5f;
    }

    // Holding a key repeats the input (the system's key repeat)
    private static bool Pressed(KeyboardKey key)
        => Raylib.IsKeyPressed(key) || Raylib.IsKeyPressedRepeat(key);

    /// <summary>Draws the rows into the given box, scrolling so the selection stays in view</summary>
    public void Draw(int x, int y, int width, int viewHeight)
    {
        int contentHeight = ContentHeight;

        _scroll = Math.Clamp(_scroll, RowOffset(_selected) + RowHeight - viewHeight, RowOffset(_selected));
        _scroll = Math.Clamp(_scroll, 0f, Math.Max(0f, contentHeight - viewHeight));

        Raylib.BeginScissorMode(x + 1, y, width - 2, viewHeight);

        int offset = 0;
        int selectable = 0;
        foreach (TuningSection section in _sections)
        {
            int headerRowY = y + offset - (int)_scroll;
            Raylib.DrawText(section.Title, x + 14, headerRowY + 8, 14, new Color(120, 200, 215, 220));
            Raylib.DrawLine(x + 14, headerRowY + 25, x + width - 14, headerRowY + 25, new Color(95, 225, 235, 60));
            offset += HeaderHeight;

            foreach (TuningSection.Entry entry in section.Entries)
            {
                DrawValueRow(entry, x, y + offset - (int)_scroll, width, _selected == selectable);
                offset += RowHeight;
                selectable++;
            }
        }

        DrawResetRow(x, y + offset - (int)_scroll, width);

        Raylib.EndScissorMode();
    }

    private static void DrawValueRow(TuningSection.Entry entry, int x, int rowY, int width, bool selected)
    {
        if (selected)
            Raylib.DrawRectangle(x + 6, rowY - 2, width - 12, RowHeight - 2, new Color(95, 225, 235, 40));

        Color textColor = selected ? new Color(210, 250, 255, 255) : new Color(200, 205, 215, 255);
        Raylib.DrawText(entry.Label, x + 16, rowY, 18, textColor);

        string value = entry.Text;
        int valueWidth = Raylib.MeasureText(value, 18);
        Raylib.DrawText(value, x + width - 20 - valueWidth, rowY, 18, textColor);
    }

    private void DrawResetRow(int x, int rowY, int width)
    {
        bool selected = IsResetRow(_selected);

        if (selected)
            Raylib.DrawRectangle(x + 6, rowY - 2, width - 12, RowHeight - 2, new Color(255, 150, 90, 45));

        if (_resetFlashTimer > 0f)
        {
            Raylib.DrawText("All values reset!", x + 16, rowY, 18, new Color(140, 240, 160, 255));
            return;
        }

        Color color = selected ? new Color(255, 190, 140, 255) : new Color(220, 160, 120, 255);
        Raylib.DrawText("Reset ALL to defaults (Enter/R)", x + 16, rowY, 18, color);
    }

    // Pixel position of a selectable row inside the content, for scrolling along
    private float RowOffset(int selectable)
    {
        int offset = 0;
        foreach (TuningSection section in _sections)
        {
            offset += HeaderHeight;
            if (selectable < section.Entries.Count) return offset + selectable * RowHeight;

            offset += section.Entries.Count * RowHeight;
            selectable -= section.Entries.Count;
        }

        return offset; // the reset row
    }
}
