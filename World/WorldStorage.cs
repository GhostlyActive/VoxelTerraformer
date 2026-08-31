using System.Numerics;
using System.IO.Compression;
using System.Text.Json;

namespace Terraformer.World;

/// <summary>
/// Der eine manuelle Spielstand: veränderte Chunks als deflate-komprimierte Dateien
/// plus eine Meta-Datei (Spielerposition, Tageszeit) im Benutzerprofil (AppData bzw. ~/.config).
/// </summary>
public sealed class WorldStorage
{
    private const byte FormatVersion = 3; // v3: Blöcke + 512-Bit-Sub-Voxel-Masken (Sculpt, 8x8x8)

    private sealed class WorldMeta
    {
        public int Version { get; set; } // fehlt bei alten Spielständen → 0 → inkompatibel
        public float PlayerX { get; set; }
        public float PlayerY { get; set; }
        public float PlayerZ { get; set; }
        public float TimeSeconds { get; set; }
    }

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Terraformer", "world");

    private string PathFor(ChunkCoord coord) => Path.Combine(_directory, $"chunk_{coord.X}_{coord.Z}.bin");

    private string MetaPath => Path.Combine(_directory, "meta.json");

    // Die Meta-Datei ist der Marker dafür, dass ein Spielstand existiert
    public bool HasSave => File.Exists(MetaPath);

    /// <summary>Spielstand vorhanden UND im aktuellen Format? Sonst würde "Load" still eine frische Welt liefern.</summary>
    public bool HasCompatibleSave => ReadMeta() is { } meta && meta.Version == FormatVersion;

    private WorldMeta? ReadMeta()
    {
        try
        {
            if (!File.Exists(MetaPath)) return null;
            return JsonSerializer.Deserialize<WorldMeta>(File.ReadAllText(MetaPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Alten Spielstand komplett entfernen (bevor ein neuer geschrieben wird)</summary>
    public void DeleteAll()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (string file in Directory.EnumerateFiles(_directory))
                File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-Effort — schlimmstenfalls bleiben verwaiste Dateien liegen
        }
    }

    public bool SaveMeta(Vector3 playerPosition, float timeSeconds)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var meta = new WorldMeta
            {
                Version = FormatVersion,
                PlayerX = playerPosition.X,
                PlayerY = playerPosition.Y,
                PlayerZ = playerPosition.Z,
                TimeSeconds = timeSeconds,
            };
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryLoadMeta(out Vector3 playerPosition, out float timeSeconds)
    {
        playerPosition = default;
        timeSeconds = 0f;

        WorldMeta? meta = ReadMeta();
        if (meta == null) return false;

        playerPosition = new Vector3(meta.PlayerX, meta.PlayerY, meta.PlayerZ);
        timeSeconds = meta.TimeSeconds;
        return true;
    }

    public bool TryLoad(ChunkCoord coord, int expectedLength, out byte[]? blocks, out Dictionary<int, ulong[]>? refinements)
    {
        blocks = null;
        refinements = null;

        try
        {
            string path = PathFor(coord);
            if (!File.Exists(path)) return false;

            using FileStream file = File.OpenRead(path);
            if (file.ReadByte() != FormatVersion) return false;

            using var inflate = new DeflateStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(inflate);

            byte[] data = reader.ReadBytes(expectedLength);
            if (data.Length != expectedLength) return false;

            int count = reader.ReadInt32();
            if (count < 0 || count > expectedLength) return false; // offensichtlich kaputt

            var loadedRefinements = new Dictionary<int, ulong[]>(count);
            for (int i = 0; i < count; i++)
            {
                int index = reader.ReadInt32();
                if (index < 0 || index >= expectedLength) return false; // korrupte Daten

                var mask = new ulong[SubVoxels.WordCount];
                for (int word = 0; word < SubVoxels.WordCount; word++)
                    mask[word] = reader.ReadUInt64();
                loadedRefinements[index] = mask;
            }

            blocks = data;
            refinements = loadedRefinements;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
        {
            // Unlesbare/korrupte Datei (InvalidDataException: kaputte Deflate-Daten)
            // → Chunk wird frisch generiert statt zu crashen
            blocks = null;
            refinements = null;
            return false;
        }
    }

    public bool Save(ChunkCoord coord, byte[] blocks, IReadOnlyDictionary<int, ulong[]> refinements)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            using FileStream file = File.Create(PathFor(coord));
            file.WriteByte(FormatVersion);

            using var deflate = new DeflateStream(file, CompressionLevel.Fastest);
            using var writer = new BinaryWriter(deflate);

            writer.Write(blocks);
            writer.Write(refinements.Count);
            foreach ((int index, ulong[] mask) in refinements)
            {
                writer.Write(index);
                for (int word = 0; word < SubVoxels.WordCount; word++)
                    writer.Write(mask[word]);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fehlschlag meldet der Aufrufer dem Spieler ("Save failed")
            return false;
        }
    }
}
