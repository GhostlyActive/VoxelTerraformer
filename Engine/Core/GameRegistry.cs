namespace VoxelEngine.Core;

/// <summary>
/// Ein Eintrag der Spieleliste. <paramref name="Id"/> ist der Ordnername unter <c>Games/</c> und
/// bestimmt damit sowohl den Asset-Pfad als auch den Spielstand-Slot.
/// </summary>
public sealed record GameEntry(string Id, string Title, string Tagline, Func<Game> Create);

/// <summary>
/// Die Spiele, die der Host anbieten kann. Wird beim Start einmal gefüllt; das Pausenmenü
/// zeigt daraus seine "Games"-Liste.
/// </summary>
public sealed class GameRegistry
{
    private readonly List<GameEntry> _entries = new();

    public IReadOnlyList<GameEntry> Entries => _entries;

    public void Add(string id, string title, string tagline, Func<Game> create)
    {
        if (_entries.Any(entry => entry.Id == id))
            throw new ArgumentException($"Spiel-Id '{id}' ist schon vergeben", nameof(id));

        _entries.Add(new GameEntry(id, title, tagline, create));
    }

    public GameEntry Find(string id)
        => _entries.FirstOrDefault(entry => entry.Id == id)
           ?? throw new ArgumentException($"Kein Spiel mit der Id '{id}' registriert", nameof(id));
}
