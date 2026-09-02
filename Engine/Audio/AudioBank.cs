using Raylib_cs;

namespace VoxelEngine.Audio;

/// <summary>
/// A game's sounds, addressed by name. Registering one first looks for <c>&lt;name&gt;.wav</c> or
/// <c>.ogg</c> in the game's <c>Assets/Sounds</c> folder; if the file is missing, the synthesized
/// stand-in takes over. A game therefore has sound without shipping any audio files, and dropping
/// a file in later replaces the stand-in without any code change.
///
/// Each sound holds several voices (aliases) so two impacts can overlap instead of cutting each
/// other off.
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

    /// <summary>False when the audio device is not available; every call then does nothing</summary>
    public bool Available { get; }

    public AudioBank(string soundDirectory)
    {
        _soundDirectory = soundDirectory;
        Available = Raylib.IsAudioDeviceReady();
    }

    /// <summary>Register a sound: the file from the sounds folder, else the synthesized stand-in</summary>
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

    /// <summary>Volume by distance: full up close, silent from <paramref name="range"/> on</summary>
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
