using Raylib_cs;
using VoxelEngine.Core;

namespace VoxelEngine.UI;

public enum PauseAction
{
    None,
    Save,
    Load,
    StartGame,
    Quit,
}

/// <summary>Result of one menu frame; <see cref="GameId"/> is only set for <see cref="PauseAction.StartGame"/></summary>
public readonly record struct PauseResult(PauseAction Action, string? GameId = null)
{
    public static readonly PauseResult None = new(PauseAction.None);
}

/// <summary>
/// The ESC menu, in two pages: the main page (continue, games, save, load, quit) and the game
/// list. While it is open the game is paused; the host reads <see cref="IsOpen"/> for that.
/// </summary>
public sealed class PauseMenu
{
    private enum Page { Root, Games }

    private readonly GameRegistry _registry;

    private Page _page = Page.Root;
    private int _selected;
    private string[] _entries = Array.Empty<string>();

    /// <summary>The game currently running; marked in the list</summary>
    public string CurrentGameId { get; set; } = "";

    /// <summary>Hides save and load when the running game has no saves</summary>
    public bool SavingAvailable { get; set; }

    /// <summary>Controls of the running game, shown below the entries</summary>
    public IReadOnlyList<string> ControlHints { get; set; } = Array.Empty<string>();

    public bool IsOpen { get; private set; }

    public PauseMenu(GameRegistry registry)
    {
        _registry = registry;
    }

    public void Close() => IsOpen = false;

    /// <summary>Handles input; reports a confirmed action exactly once</summary>
    public PauseResult Update()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            // From the game list, ESC goes back one level instead of straight into the game
            if (IsOpen && _page == Page.Games) OpenPage(Page.Root);
            else
            {
                IsOpen = !IsOpen;
                if (IsOpen) OpenPage(Page.Root);
            }

            return PauseResult.None;
        }

        if (!IsOpen) return PauseResult.None;

        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _selected = (_selected + 1) % _entries.Length;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) _selected = (_selected - 1 + _entries.Length) % _entries.Length;

        bool confirm = Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter);
        if (!confirm) return PauseResult.None;

        return _page == Page.Games ? ConfirmGames() : ConfirmRoot();
    }

    private PauseResult ConfirmRoot()
    {
        switch (RootEntries()[_selected])
        {
            case "Continue":
                IsOpen = false;
                return PauseResult.None;

            case "Games":
                OpenPage(Page.Games);
                return PauseResult.None;

            case "Save world":
                return new PauseResult(PauseAction.Save);

            case "Load world":
                return new PauseResult(PauseAction.Load);

            default:
                return new PauseResult(PauseAction.Quit);
        }
    }

    private PauseResult ConfirmGames()
    {
        if (_selected >= _registry.Entries.Count)
        {
            OpenPage(Page.Root);
            return PauseResult.None;
        }

        GameEntry entry = _registry.Entries[_selected];
        if (entry.Id == CurrentGameId)
        {
            // Restarting the same game would throw away the running world, so just resume
            IsOpen = false;
            return PauseResult.None;
        }

        IsOpen = false;
        return new PauseResult(PauseAction.StartGame, entry.Id);
    }

    private void OpenPage(Page page)
    {
        _page = page;
        _selected = 0;
        _entries = page == Page.Games ? GameEntries() : RootEntries();

        if (page != Page.Games) return;

        // Start on the running game rather than at the top of the list
        for (int i = 0; i < _registry.Entries.Count; i++)
            if (_registry.Entries[i].Id == CurrentGameId)
                _selected = i;
    }

    private string[] RootEntries()
    {
        var entries = new List<string> { "Continue", "Games" };
        if (SavingAvailable)
        {
            entries.Add("Save world");
            entries.Add("Load world");
        }
        entries.Add("Quit");

        return entries.ToArray();
    }

    private string[] GameEntries()
        => _registry.Entries.Select(entry => entry.Title).Append("Back").ToArray();

    public void Draw(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;

        // dark veil over the frozen game
        Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(5, 8, 14, 150));

        bool showHints = _page == Page.Root && ControlHints.Count > 0;

        int width = _page == Page.Games ? 460 : 520;
        int rowHeight = _page == Page.Games ? 46 : 34;
        int hintsHeight = showHints ? 14 + ControlHints.Count * 20 : 0;
        int height = 96 + _entries.Length * rowHeight + hintsHeight;
        int x = (screenWidth - width) / 2;
        int y = (screenHeight - height) / 2;

        Raylib.DrawRectangle(x, y, width, height, new Color(10, 14, 22, 235));
        Raylib.DrawRectangleLines(x, y, width, height, new Color(95, 225, 235, 200));

        string title = _page == Page.Games ? "GAMES" : "PAUSED";
        string hint = _page == Page.Games ? "Arrows + Enter | ESC: back" : "Arrows + Enter | ESC: resume";

        Raylib.DrawText(title, x + 20, y + 16, 30, new Color(95, 225, 235, 255));
        Raylib.DrawText(hint, x + 20, y + 52, 14, new Color(180, 190, 200, 255));

        for (int i = 0; i < _entries.Length; i++)
        {
            int rowY = y + 84 + i * rowHeight;
            bool selected = i == _selected;

            if (selected)
                Raylib.DrawRectangle(x + 10, rowY - 4, width - 20, rowHeight - 4, new Color(95, 225, 235, 40));

            Color color = selected ? new Color(210, 250, 255, 255) : new Color(200, 205, 215, 255);
            Raylib.DrawText(_entries[i], x + 24, rowY, 24, color);

            if (_page != Page.Games || i >= _registry.Entries.Count) continue;

            GameEntry entry = _registry.Entries[i];
            Raylib.DrawText(entry.Tagline, x + 24, rowY + 24, 14, new Color(150, 160, 175, 255));

            if (entry.Id != CurrentGameId) continue;

            const string marker = "running";
            int markerWidth = Raylib.MeasureText(marker, 14);
            Raylib.DrawText(marker, x + width - 24 - markerWidth, rowY + 6, 14, new Color(140, 240, 160, 255));
        }

        if (!showHints) return;

        int hintsTop = y + 84 + _entries.Length * rowHeight + 4;
        Raylib.DrawLine(x + 20, hintsTop, x + width - 20, hintsTop, new Color(95, 225, 235, 60));

        for (int i = 0; i < ControlHints.Count; i++)
            Raylib.DrawText(ControlHints[i], x + 24, hintsTop + 12 + i * 20, 15, new Color(160, 172, 188, 255));
    }
}
