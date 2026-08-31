using Raylib_cs;

namespace Terraformer.UI;

public enum PauseMenuAction
{
    None,
    Save,
    Load,
    Quit,
}

/// <summary>
/// ESC-Menü: Fortsetzen, Speichern, Laden, Beenden. Tastatursteuerung wie das Debug-Menü.
/// Solange es offen ist, pausiert das Spiel (steuert Program über IsOpen).
/// </summary>
public sealed class PauseMenu
{
    private static readonly string[] _entries = { "Continue", "Save world", "Load world", "Quit" };

    private int _selected;
    private string _statusText = "";
    private float _statusTimer;

    public bool IsOpen { get; private set; }

    public void Close() => IsOpen = false;

    public void ShowStatus(string text)
    {
        _statusText = text;
        _statusTimer = 2.5f;
    }

    /// <summary>Verarbeitet Eingaben; liefert die bestätigte Aktion genau einmal</summary>
    public PauseMenuAction Update()
    {
        _statusTimer = Math.Max(0f, _statusTimer - Raylib.GetFrameTime());

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            IsOpen = !IsOpen;
            _selected = 0;
            return PauseMenuAction.None;
        }

        if (!IsOpen) return PauseMenuAction.None;

        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _selected = (_selected + 1) % _entries.Length;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) _selected = (_selected - 1 + _entries.Length) % _entries.Length;

        bool confirm = Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter);
        if (!confirm) return PauseMenuAction.None;

        switch (_selected)
        {
            case 0:
                IsOpen = false;
                return PauseMenuAction.None;
            case 1:
                return PauseMenuAction.Save;
            case 2:
                return PauseMenuAction.Load;
            default:
                return PauseMenuAction.Quit;
        }
    }

    public void Draw(int screenWidth, int screenHeight)
    {
        if (IsOpen)
        {
            // dunkler Schleier über dem eingefrorenen Spiel
            Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(5, 8, 14, 140));

            const int width = 320;
            int height = 96 + _entries.Length * 34;
            int x = (screenWidth - width) / 2;
            int y = (screenHeight - height) / 2;

            Raylib.DrawRectangle(x, y, width, height, new Color(10, 14, 22, 230));
            Raylib.DrawRectangleLines(x, y, width, height, new Color(95, 225, 235, 200));

            Raylib.DrawText("PAUSED", x + 20, y + 16, 30, new Color(95, 225, 235, 255));
            Raylib.DrawText("Arrows + Enter | ESC: back", x + 20, y + 52, 14, new Color(180, 190, 200, 255));

            for (int i = 0; i < _entries.Length; i++)
            {
                int rowY = y + 84 + i * 34;
                bool selected = i == _selected;

                if (selected)
                    Raylib.DrawRectangle(x + 10, rowY - 4, width - 20, 30, new Color(95, 225, 235, 40));

                Color color = selected ? new Color(210, 250, 255, 255) : new Color(200, 205, 215, 255);
                Raylib.DrawText(_entries[i], x + 24, rowY, 24, color);
            }
        }

        if (_statusTimer > 0f)
        {
            int textWidth = Raylib.MeasureText(_statusText, 20);
            int x = (screenWidth - textWidth) / 2;
            int y = screenHeight - 80;
            Raylib.DrawText(_statusText, x + 1, y + 1, 20, new Color(10, 15, 25, 200));
            Raylib.DrawText(_statusText, x, y, 20, new Color(140, 240, 160, 255));
        }
    }
}
