using Raylib_cs;

namespace VoxelEngine.Audio;

/// <summary>
/// Die Sounds eines Spiels, angesprochen über einen Namen. Beim Anmelden wird zuerst im
/// <c>Assets/Sounds</c>-Ordner des Spiels nach <c>&lt;name&gt;.wav</c> bzw. <c>.ogg</c> gesucht;
/// fehlt die Datei, springt der synthetische Ersatzklang ein. Ein Spiel klingt dadurch auch
/// ohne mitgelieferte Audiodateien, und eine später hinzugelegte Datei ersetzt den Ersatz,
/// ohne dass sich Code ändert.
///
/// Jeder Sound hält mehrere Stimmen (Aliase), damit sich zwei Einschläge überlagern können,
/// statt einander abzuschneiden.
/// </summary>
public sealed class AudioBank : IDisposable
{
    private const int VoicesPerSound = 4;

    private sealed class Entry
    {
        public required Sound Source { get; init; }
        public required Sound[] Voices { get; init; }
        public int NextVoice;
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly string _soundDirectory;

    /// <summary>False, wenn das Audiogerät nicht bereitsteht — alle Aufrufe laufen dann ins Leere</summary>
    public bool Available { get; }

    public AudioBank(string soundDirectory)
    {
        _soundDirectory = soundDirectory;
        Available = Raylib.IsAudioDeviceReady();
    }

    /// <summary>Sound anmelden: Datei aus dem Sounds-Ordner, sonst der synthetische Ersatz</summary>
    public void Define(string name, SfxShape fallback)
    {
        if (!Available || _entries.ContainsKey(name)) return;

        Sound source = TryLoadFile(name) ?? SfxSynth.Create(fallback);
        if (!Raylib.IsSoundValid(source)) return;

        var voices = new Sound[VoicesPerSound];
        for (int i = 0; i < voices.Length; i++)
            voices[i] = Raylib.LoadSoundAlias(source);

        _entries[name] = new Entry { Source = source, Voices = voices };
    }

    public void Play(string name, float volume = 1f, float pitch = 1f)
    {
        if (!Available || !_entries.TryGetValue(name, out Entry? entry)) return;

        Sound voice = entry.Voices[entry.NextVoice];
        entry.NextVoice = (entry.NextVoice + 1) % entry.Voices.Length;

        Raylib.SetSoundVolume(voice, Math.Clamp(volume, 0f, 1f));
        Raylib.SetSoundPitch(voice, Math.Clamp(pitch, 0.25f, 4f));
        Raylib.PlaySound(voice);
    }

    /// <summary>Lautstärke nach Entfernung: nah voll, ab <paramref name="range"/> still</summary>
    public void PlayAt(string name, float distance, float range, float volume = 1f, float pitch = 1f)
    {
        float attenuation = 1f - Math.Clamp(distance / MathF.Max(range, 0.01f), 0f, 1f);
        if (attenuation <= 0.01f) return;

        Play(name, volume * attenuation * attenuation, pitch);
    }

    private Sound? TryLoadFile(string name)
    {
        foreach (string extension in new[] { ".wav", ".ogg", ".mp3" })
        {
            string path = Path.Combine(_soundDirectory, name + extension);
            if (!File.Exists(path)) continue;

            Sound loaded = Raylib.LoadSound(path);
            if (Raylib.IsSoundValid(loaded)) return loaded;
        }

        return null;
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values)
        {
            foreach (Sound voice in entry.Voices)
                Raylib.UnloadSoundAlias(voice);

            Raylib.UnloadSound(entry.Source);
        }

        _entries.Clear();
    }
}
