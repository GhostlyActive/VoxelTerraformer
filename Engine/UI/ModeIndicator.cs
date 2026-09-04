using Raylib_cs;
using VoxelEngine.Rendering;
using VoxelEngine.World;

namespace VoxelEngine.UI;

/// <summary>
/// Three pills for the three voxel modes, the active one lit in its colour and popping briefly
/// when it changes, plus a thin bar under them while the world is still switching over. The
/// engine draws the element; where it sits and which key it names is the game's decision.
/// </summary>
public static class ModeIndicator
{
    private const int PillWidth = 96;
    private const int PillHeight = 26;
    private const int Gap = 6;
    private const int FontSize = 16;

    /// <summary>
    /// <paramref name="switchAge"/> is the time since the last switch in seconds (large when
    /// idle); <paramref name="progress01"/> and <paramref name="converting"/> describe the wave.
    /// </summary>
    public static void Draw(int centerX, int y, TerrainMode mode, float switchAge, float progress01, bool converting, string? keyHint)
    {
        int total = 3 * PillWidth + 2 * Gap;
        int left = centerX - total / 2;

        // The pop: the active pill starts a fifth larger and settles within a third of a second
        float t = Math.Clamp(switchAge / 0.35f, 0f, 1f);
        float pop = 1f + 0.18f * (1f - t) * (1f - t);

        for (int i = 0; i < 3; i++)
        {
            var candidate = (TerrainMode)i;
            bool active = candidate == mode;

            int x = left + i * (PillWidth + Gap);
            int width = active ? (int)(PillWidth * pop) : PillWidth;
            int height = active ? (int)(PillHeight * pop) : PillHeight;
            int px = x + (PillWidth - width) / 2;
            int py = y + (PillHeight - height) / 2;
            var rect = new Rectangle(px, py, width, height);

            Color color = ModeColors.Of(candidate);
            Color fill = active ? new Color(color.R, color.G, color.B, (byte)70) : new Color(10, 14, 22, 170);
            Color outline = active ? color : new Color(95, 225, 235, 90);
            Color text = active ? new Color(240, 250, 255, 255) : new Color(170, 180, 190, 255);

            Raylib.DrawRectangleRounded(rect, 0.5f, 8, fill);
            Raylib.DrawRectangleRoundedLines(rect, 0.5f, 8, outline);

            string label = ModeColors.Label(candidate);
            int textWidth = Raylib.MeasureText(label, FontSize);
            Raylib.DrawText(label, px + (width - textWidth) / 2 + 1, py + (height - FontSize) / 2 + 1, FontSize, Hud.Shadow);
            Raylib.DrawText(label, px + (width - textWidth) / 2, py + (height - FontSize) / 2, FontSize, text);
        }

        if (keyHint != null)
        {
            string hint = $"{keyHint}: switch";
            Hud.Text(hint, left + total + 10, y + (PillHeight - 14) / 2, 14, new Color(170, 180, 190, 255));
        }

        if (!converting) return;

        // The wave's progress, so the player sees the world is still turning over
        int barY = y + PillHeight + 4;
        Raylib.DrawRectangle(left, barY, total, 3, new Color(10, 14, 22, 170));
        Raylib.DrawRectangle(left, barY, (int)(total * Math.Clamp(progress01, 0f, 1f)), 3, ModeColors.Of(mode));
    }
}
