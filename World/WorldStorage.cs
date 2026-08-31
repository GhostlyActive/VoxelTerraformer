using System.IO.Compression;

namespace Terraformer.World;

/// <summary>
/// Speichert veränderte Chunks als deflate-komprimierte Dateien im Benutzerprofil
/// (AppData bzw. ~/.config) — eine Datei pro Chunk, läuft auf allen Plattformen.
/// </summary>
public sealed class WorldStorage
{
    private const byte FormatVersion = 1;

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Terraformer", "world");

    private string PathFor(ChunkCoord coord) => Path.Combine(_directory, $"chunk_{coord.X}_{coord.Z}.bin");

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
