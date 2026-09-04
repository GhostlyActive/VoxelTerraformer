using VoxelEngine.Config;

namespace Games.RocketStorm;

/// <summary>One run that ended</summary>
public sealed class HighscoreEntry
{
    public int Score { get; set; }
    public int Wave { get; set; }
    public float Seconds { get; set; }
    public string Date { get; set; } = "";
}

/// <summary>
/// The best runs on this machine, kept in the settings store next to the game's dials. Ten
/// entries, best first; a new run is slotted in where it belongs.
/// </summary>
public sealed class Highscores
{
    private const string Key = "RocketStorm/Highscores";
    private const int Capacity = 10;

    private readonly SettingsStore _store;

    public List<HighscoreEntry> Entries { get; }

    public Highscores(SettingsStore store)
    {
        _store = store;
        Entries = store.Load<List<HighscoreEntry>>(Key, () => new List<HighscoreEntry>());
        Entries.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    public int Best => Entries.Count > 0 ? Entries[0].Score : 0;

    /// <summary>Records a run; returns its rank (1 = best) or 0 when it did not make the list</summary>
    public int Record(int score, int wave, float seconds)
    {
        var entry = new HighscoreEntry { Score = score, Wave = wave, Seconds = seconds, Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };

        int rank = Entries.FindIndex(other => score > other.Score);
        if (rank < 0) rank = Entries.Count;
        if (rank >= Capacity) return 0;

        Entries.Insert(rank, entry);
        if (Entries.Count > Capacity) Entries.RemoveAt(Entries.Count - 1);

        _store.Save(Key, Entries);
        return rank + 1;
    }
}
