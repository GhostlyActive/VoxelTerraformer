using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoxelEngine.Config;

/// <summary>
/// One JSON file with a section per owner: the engine's own dials, the terrain scene's dials
/// per game, whatever a game registers. Sections are typed objects that load and save
/// independently, so a game's tuning survives restarts without the engine knowing its fields.
///
/// A file from before the split holds one flat object; it seeds every section that asks, so
/// nothing the player tuned is lost on the first start after an update.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly JsonObject _root;
    private readonly JsonObject? _legacy;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
        _root = new JsonObject();

        try
        {
            if (File.Exists(filePath) && JsonNode.Parse(File.ReadAllText(filePath)) is JsonObject parsed)
            {
                if (parsed.ContainsKey("Sections")) _root = parsed;
                else _legacy = parsed; // flat file from before the sections
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable or broken file: carry on with the defaults instead of crashing
        }

        _root["Sections"] ??= new JsonObject();
    }

    private JsonObject Sections => (JsonObject)_root["Sections"]!;

    /// <summary>The section as a typed object; the defaults, or the old flat file, when it is missing</summary>
    public T Load<T>(string section) where T : new()
        => Load(section, () => new T());

    /// <summary>The section as a typed object, or what <paramref name="defaults"/> makes when it is missing or broken</summary>
    public T Load<T>(string section, Func<T> defaults)
    {
        JsonNode? node = Sections[section] ?? _legacy;
        if (node == null) return defaults();

        try
        {
            return node.Deserialize<T>(Options) ?? defaults();
        }
        catch (JsonException)
        {
            return defaults();
        }
    }

    /// <summary>Replaces the section and writes the file</summary>
    public void Save<T>(string section, T value)
    {
        Sections[section] = JsonSerializer.SerializeToNode(value, Options);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, _root.ToJsonString(Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Saving is a convenience; the game runs fine without it
        }
    }
}
