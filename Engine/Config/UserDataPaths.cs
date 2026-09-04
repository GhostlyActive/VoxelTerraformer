namespace VoxelEngine.Config;

/// <summary>
/// Where a product built on the engine keeps its files in the user's profile: the tuning
/// settings and one save folder per slot. Keyed on the product name, so two products built on
/// the same engine never write into each other's folders.
/// </summary>
public sealed class UserDataPaths
{
    public string ProductName { get; }

    /// <summary>The product's folder under the platform's application-data directory</summary>
    public string Root { get; }

    public UserDataPaths(string productName)
    {
        ProductName = productName;
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), productName);
    }

    // File name from before the engine split; kept so existing settings do not silently fall
    // back to the defaults
    public string SettingsFile => Path.Combine(Root, "debug-settings.json");

    public string SaveDirectory(string slot) => Path.Combine(Root, slot);
}
