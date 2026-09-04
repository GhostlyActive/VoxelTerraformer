using Raylib_cs;

namespace VoxelEngine.UI;

/// <summary>
/// One group of dials in the tuning menu, added by whoever owns the values: the engine, a scene,
/// a game. Removing the section saves it, so a scene that goes away leaves its tuning behind.
/// </summary>
public sealed class TuningSection
{
    internal sealed record Entry(
        string Label,
        Func<float> Get,
        Action<float> Set,
        float Default,
        float Step,
        float Min,
        float Max,
        string Format,
        Func<float, string>? Display = null)
    {
        public string Text => Display != null ? Display(Get()) : Get().ToString(Format);
    }

    internal readonly List<Entry> Entries = new();

    public string Title { get; }

    internal Action? OnSave { get; }

    internal TuningSection(string title, Action? onSave)
    {
        Title = title;
        OnSave = onSave;
    }

    public TuningSection Value(string label, Func<float> get, Action<float> set, float defaultValue, float step, float min, float max, string format = "0.00")
    {
        Entries.Add(new Entry(label, get, set, defaultValue, step, min, max, format));
        return this;
    }

    public TuningSection Toggle(string label, Func<bool> get, Action<bool> set, bool defaultValue)
    {
        Entries.Add(new Entry(label, () => get() ? 1f : 0f, v => set(v >= 0.5f), defaultValue ? 1f : 0f, 1f, 0f, 1f, "0",
            v => v >= 0.5f ? "on" : "off"));
        return this;
    }

    /// <summary>One of several named options, stepped through with the arrow keys</summary>
    public TuningSection Choice(string label, string[] options, Func<int> get, Action<int> set, int defaultIndex)
    {
        Entries.Add(new Entry(label, () => get(), v => set((int)MathF.Round(v)), defaultIndex, 1f, 0f, options.Length - 1, "0",
            v => options[Math.Clamp((int)MathF.Round(v), 0, options.Length - 1)]));
        return this;
    }
}

/// <summary>
/// The tuning menu on key M, for the dials of the running scene and game. The game keeps running
/// underneath so a change can be felt right away; closing saves. Anything that applies to every
/// game (display, mouse, view distance) lives in the pause menu's settings page instead.
/// </summary>
public sealed class TuningMenu
{
    private readonly SettingsPanel _panel = new();

    public bool IsOpen { get; private set; }

    /// <summary>Adds a group of dials; <paramref name="onSave"/> runs when the menu closes or the section is removed</summary>
    public TuningSection AddSection(string title, Action? onSave = null) => _panel.AddSection(title, onSave);

    /// <summary>Takes a section out again, saving it first</summary>
    public void RemoveSection(TuningSection section) => _panel.RemoveSection(section);

    public void Update()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.M))
        {
            IsOpen = !IsOpen;
            if (!IsOpen) _panel.SaveAll();
        }

        if (IsOpen) _panel.Update();
    }

    public void Draw(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;

        const int width = 400;
        const int headerY = 40;
        const int bodyTop = 58;

        int x = screenWidth - width - 16;
        int maxHeight = screenHeight - headerY - 24;
        int height = Math.Min(bodyTop + _panel.ContentHeight + 10, maxHeight);

        Raylib.DrawRectangle(x, headerY, width, height, new Color(10, 14, 22, 214));
        Raylib.DrawRectangleLines(x, headerY, width, height, new Color(95, 225, 235, 200));

        Raylib.DrawText("TUNING", x + 12, headerY + 10, 20, new Color(95, 225, 235, 255));
        Raylib.DrawText("Arrows: navigate + adjust | R: reset | M: save", x + 12, headerY + 34, 14, new Color(180, 190, 200, 255));

        _panel.Draw(x, headerY + bodyTop, width, height - bodyTop - 10);
    }
}
