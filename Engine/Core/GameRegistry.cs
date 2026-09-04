using System.Reflection;

namespace VoxelEngine.Core;

/// <summary>
/// One entry in the game list. <paramref name="Id"/> is the folder name under <c>Games/</c>, and
/// therefore decides the asset path; the save slot is the game's own choice.
/// </summary>
public sealed record GameEntry(string Id, string Title, string Tagline, Func<Game> Create);

/// <summary>
/// The games the host can offer. Filled once at startup, by hand or by scanning assemblies for
/// <see cref="GameDefinitionAttribute"/>; the pause menu builds its "Games" list from it.
/// </summary>
public sealed class GameRegistry
{
    private readonly List<GameEntry> _entries = new();

    public IReadOnlyList<GameEntry> Entries => _entries;

    public void Add(string id, string title, string tagline, Func<Game> create)
    {
        if (_entries.Any(entry => entry.Id == id))
            throw new ArgumentException($"Game id '{id}' is already taken", nameof(id));

        _entries.Add(new GameEntry(id, title, tagline, create));
    }

    /// <summary>Adds one game class by its <see cref="GameDefinitionAttribute"/></summary>
    public void Add<TGame>() where TGame : Game, new()
        => Add(typeof(TGame));

    private void Add(Type type)
    {
        GameDefinitionAttribute definition = type.GetCustomAttribute<GameDefinitionAttribute>()
            ?? throw new ArgumentException($"{type.FullName} carries no [GameDefinition]", nameof(type));

        if (!typeof(Game).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
            throw new ArgumentException($"{type.FullName} must derive from Game and have a parameterless constructor", nameof(type));

        Add(definition.Id, definition.Title, definition.Tagline, () => (Game)Activator.CreateInstance(type)!);
    }

    /// <summary>Adds every class in the assembly that carries a <see cref="GameDefinitionAttribute"/></summary>
    public void AddFrom(Assembly assembly)
    {
        IEnumerable<Type> games = assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<GameDefinitionAttribute>() != null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (Type type in games)
            Add(type);
    }

    /// <summary>
    /// Adds the games of every assembly the launcher was built with, as recorded by the
    /// <see cref="GameAssemblyAttribute"/> entries the build stamped onto it. Ordered by assembly
    /// name, so the menu is stable.
    /// </summary>
    public void AddFromLauncher(Assembly launcher)
    {
        IEnumerable<string> names = launcher.GetCustomAttributes<GameAssemblyAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (string name in names)
            AddFrom(Assembly.Load(new AssemblyName(name)));
    }

    public GameEntry Find(string id)
        => _entries.FirstOrDefault(entry => entry.Id == id)
           ?? throw new ArgumentException($"No game registered with id '{id}'", nameof(id));
}
