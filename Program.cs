using System.Reflection;
using VoxelEngine.Core;

namespace Terraformer;

/// <summary>
/// Entry point: the launcher that plays whatever games were built next to it. It knows none of
/// them by name: every project under <c>Games/</c> is referenced by the build and announces its
/// games with <see cref="GameDefinitionAttribute"/>.
/// </summary>
public static class Program
{
    private const string DefaultGame = "FreeWalk";

    public static void Main(string[] args)
    {
        var registry = new GameRegistry();
        registry.AddFromLauncher(Assembly.GetExecutingAssembly());

        var options = new HostOptions
        {
            // Folder name for settings and saves in the user profile; kept from the first release
            ProductName = "Terraformer",
            WindowTitle = "Terraformer",

            // Smoke test: render for a few seconds, drop a screenshot, quit.
            // "--smoke 600" runs longer, which is how the frame timings at a fully loaded view get measured.
            SmokeFrames = SmokeFrames(args),

            // Benchmark: Free Walk drives the camera through a fixed script and prints frame
            // statistics per phase; the game quits on its own (the frame cap is a safety net)
            Benchmark = HasFlag(args, "--bench"),
        };

        using var host = new GameHost(registry, options);
        host.Run(StartGameId(args, registry));
    }

    /// <summary>--game &lt;Id&gt; starts straight into a game; without it, the sandbox</summary>
    private static string StartGameId(string[] args, GameRegistry registry)
    {
        int index = Array.IndexOf(args, "--game");
        if (index < 0 || index + 1 >= args.Length) return DefaultGame;

        string requested = args[index + 1];
        GameEntry? match = registry.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Id, requested, StringComparison.OrdinalIgnoreCase));

        if (match != null) return match.Id;

        Console.Error.WriteLine($"Unknown game '{requested}'. Known: {string.Join(", ", registry.Entries.Select(entry => entry.Id))}");
        return DefaultGame;
    }

    private static bool HasFlag(string[] args, string flag)
        => Array.Exists(args, argument => argument == flag);

    private static int SmokeFrames(string[] args)
    {
        if (HasFlag(args, "--bench")) return 100_000;

        int index = Array.IndexOf(args, "--smoke");
        if (index < 0) return 0;

        return index + 1 < args.Length && int.TryParse(args[index + 1], out int frames) && frames > 0 ? frames : 150;
    }
}
