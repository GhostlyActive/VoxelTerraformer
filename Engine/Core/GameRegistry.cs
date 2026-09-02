namespace VoxelEngine.Core;

/// <summary>
/// One entry in the game list. <paramref name="Id"/> is the folder name under <c>Games/</c>, and
/// therefore decides both the asset path and the save slot.
/// </summary>
public sealed record GameEntry(string Id, string Title, string Tagline, Func<Game> Create);

/// <summary>
/// The games the host can offer. Filled once at startup; the pause menu builds its "Games" list
/// from it.
/// </summary>
public sealed class GameRegistry
{
    private readonly List<GameEntry> _entries = new();

    public IReadOnlyList<GameEntry> Entries => _entries;

    public void Add(string id, string title, string tagline, Func<Game> create)
    {
        if (_entries.Any(entry => entry.Id == id))
            throw new ArgumentException($"Game id '{id}' is already taken", nameof(id));

        _entries.Add(new GameEntry(id, title, tagline, create));
    }

    public GameEntry Find(string id)
        => _entries.FirstOrDefault(entry => entry.Id == id)
           ?? throw new ArgumentException($"No game registered with id '{id}'", nameof(id));
}
