using Raylib_cs;

namespace VoxelEngine.UI;

/// <summary>
/// Building blocks for the in-game readouts. Text always gets a dark offset behind it: without
/// one, light lettering disappears against bright sky or snow.
/// </summary>
public static class Hud
{
    public static readonly Color Ink = new(220, 245, 250, 240);
    public static readonly Color Shadow = new(10, 15, 25, 200);
    public static readonly Color Accent = new(95, 225, 235, 255);
    public static readonly Color Warning = new(255, 150, 90, 255);

    public static void Text(string text, int x, int y, int size, Color? color = null)
    {
        Raylib.DrawText(text, x + 1, y + 1, size, Shadow);
        Raylib.DrawText(text, x, y, size, color ?? Ink);
    }

    public static void Centered(string text, int centerX, int y, int size, Color? color = null)
        => Text(text, centerX - Raylib.MeasureText(text, size) / 2, y, size, color);

    public static void Crosshair(int screenWidth, int screenHeight, Color? color = null)
    {
        int x = screenWidth / 2;
        int y = screenHeight / 2;

        Raylib.DrawCircle(x, y, 4f, Shadow);
        Raylib.DrawCircle(x, y, 2.5f, color ?? Ink);
    }

    /// <summary>Bar for health, heat or progress; <paramref name="fill"/> in 0..1</summary>
    public static void Bar(int x, int y, int width, int height, float fill, Color color, string? label = null)
    {
        Raylib.DrawRectangle(x, y, width, height, new Color(10, 14, 22, 190));
        Raylib.DrawRectangle(x, y, (int)(width * Math.Clamp(fill, 0f, 1f)), height, color);
        Raylib.DrawRectangleLines(x, y, width, height, new Color(95, 225, 235, 120));

        if (label != null) Text(label, x + 6, y + height / 2 - 8, 16);
    }

    public static void Panel(int x, int y, int width, int height)
    {
        Raylib.DrawRectangle(x, y, width, height, new Color(10, 14, 22, 190));
        Raylib.DrawRectangleLines(x, y, width, height, new Color(95, 225, 235, 110));
    }
}
