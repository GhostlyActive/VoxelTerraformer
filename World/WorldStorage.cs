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
    private const byte FormatVersion = 1;

    private sealed class WorldMeta
    {
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

    public void SaveMeta(Vector3 playerPosition, float timeSeconds)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var meta = new WorldMeta
            {
                PlayerX = playerPosition.X,
                PlayerY = playerPosition.Y,
                PlayerZ = playerPosition.Z,
                TimeSeconds = timeSeconds,
            };
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public bool TryLoadMeta(out Vector3 playerPosition, out float timeSeconds)
    {
        playerPosition = default;
        timeSeconds = 0f;

        try
        {
            if (!File.Exists(MetaPath)) return false;

            WorldMeta? meta = JsonSerializer.Deserialize<WorldMeta>(File.ReadAllText(MetaPath));
            if (meta == null) return false;

            playerPosition = new Vector3(meta.PlayerX, meta.PlayerY, meta.PlayerZ);
            timeSeconds = meta.TimeSeconds;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public bool TryLoad(ChunkCoord coord, int expectedLength, out byte[]? blocks)
    {
        blocks = null;

        try
        {
            string path = PathFor(coord);
            if (!File.Exists(path)) return false;

            using FileStream file = File.OpenRead(path);
            if (file.ReadByte() != FormatVersion) return false;

            using var inflate = new DeflateStream(file, CompressionMode.Decompress);
            var data = new byte[expectedLength];
            inflate.ReadExactly(data);

            blocks = data;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            // Unlesbare Datei → Chunk wird frisch generiert statt zu crashen
            blocks = null;
            return false;
        }
    }

    public void Save(ChunkCoord coord, byte[] blocks)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            using FileStream file = File.Create(PathFor(coord));
            file.WriteByte(FormatVersion);

            using var deflate = new DeflateStream(file, CompressionLevel.Fastest);
            deflate.Write(blocks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Speichern ist Best-Effort — das Spiel läuft weiter
        }
    }
}
