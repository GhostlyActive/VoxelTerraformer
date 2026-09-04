namespace VoxelEngine.Core;

/// <summary>
/// Marks a <see cref="Game"/> class as a game the launcher can start and gives it its identity:
/// the id is the folder name under <c>Games/</c> (and so the asset path), the title and tagline
/// are what the pause menu shows. The metadata lives with the game, not in the launcher.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GameDefinitionAttribute : Attribute
{
    public string Id { get; }
    public string Title { get; }
    public string Tagline { get; }

    public GameDefinitionAttribute(string id, string title, string tagline)
    {
        Id = id;
        Title = title;
        Tagline = tagline;
    }
}

/// <summary>
/// Stamped onto the launcher assembly once per game project it references, by the build. That is
/// how the launcher finds its games without a directory scan, which would not survive a
/// single-file publish, and without a hand-written list.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GameAssemblyAttribute : Attribute
{
    public string AssemblyName { get; }

    public GameAssemblyAttribute(string assemblyName) => AssemblyName = assemblyName;
}
