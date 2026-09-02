using System.Numerics;
using System.IO.Compression;
using System.Text.Json;

namespace VoxelEngine.World;

/// <summary>
/// One manual save per slot: the changed chunks as deflate-compressed files plus a meta file
/// (player position, time of day) in the user profile (AppData or ~/.config). Every game gets its
/// own slot and therefore never overwrites another one's.
/// </summary>
public sealed class WorldStorage
{
    private const byte FormatVersion = 4; // v4: blocks plus sub-voxel density field (512 bytes per edited block)

    private sealed class WorldMeta
    {
        public int Version { get; set; } // missing in old saves, so 0, so incompatible
        public float PlayerX { get; set; }
        public float PlayerY { get; set; }
        public float PlayerZ { get; set; }
        public float TimeSeconds { get; set; }
    }

    private readonly string _directory;

    public WorldStorage(string slot)
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Terraformer", slot);
    }

    private string PathFor(ChunkCoord coord) => Path.Combine(_directory, $"chunk_{coord.X}_{coord.Z}.bin");

    private string MetaPath => Path.Combine(_directory, "meta.json");

    // The meta file is the marker that a save exists
    public bool HasSave => File.Exists(MetaPath);

    /// <summary>Is there a save AND is it in the current format? Otherwise "Load" would quietly hand back a fresh world.</summary>
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

    /// <summary>Remove an old save completely, before a new one is written</summary>
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
            // Best effort; at worst some orphaned files stay behind
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

    public bool TryLoad(ChunkCoord coord, int expectedLength, out byte[]? blocks, out Dictionary<int, byte[]>? refinements)
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
            if (count < 0 || count > expectedLength) return false; // clearly broken

            var loadedRefinements = new Dictionary<int, byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int index = reader.ReadInt32();
                if (index < 0 || index >= expectedLength) return false; // corrupt data

                byte[] field = reader.ReadBytes(SubVoxels.CellCount);
                if (field.Length != SubVoxels.CellCount) return false;
                loadedRefinements[index] = field;
            }

            blocks = data;
            refinements = loadedRefinements;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
        {
            // Unreadable or corrupt file (InvalidDataException: broken deflate data):
            // the chunk is generated fresh instead of crashing
            blocks = null;
            refinements = null;
            return false;
        }
    }

    public bool Save(ChunkCoord coord, byte[] blocks, IReadOnlyDictionary<int, byte[]> refinements)
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
            foreach ((int index, byte[] field) in refinements)
            {
                writer.Write(index);
                writer.Write(field);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller reports the failure to the player ("Save failed")
            return false;
        }
    }
}
