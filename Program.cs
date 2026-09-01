using Terraformer.Games.FreeWalk;
using Terraformer.Games.RocketStorm;
using Terraformer.Games.SolarSystem;
using VoxelEngine.Core;

namespace Terraformer;

/// <summary>
/// Einstiegspunkt: meldet die Spiele an und übergibt an den Host. Ein weiteres Spiel braucht
/// genau eine Zeile hier plus seinen Ordner unter <c>Games/</c> — die Engine kennt keines davon.
/// </summary>
public static class Program
{
    private const string DefaultGame = "FreeWalk";

    public static void Main(string[] args)
    {
        var registry = new GameRegistry();

        registry.Add("FreeWalk", "Free Walk",
            "Sandbox: build, dig and switch between the voxel modes",
            () => new FreeWalkGame());

        registry.Add("RocketStorm", "Rocket Storm",
            "Survive the barrage - you dig your own cover",
            () => new RocketStormGame());

        registry.Add("SolarSystem", "Solar System",
            "Fly out and blast the planets back into voxels",
            () => new SolarSystemGame());

        var options = new HostOptions
        {
            WindowTitle = "Terraformer",

            // Rauchtest: ein paar Sekunden rendern, Screenshot ablegen, beenden
            SmokeFrames = HasFlag(args, "--smoke") ? 150 : 0,
        };

        using var host = new GameHost(registry, options);
        host.Run(StartGameId(args, registry));
    }

    /// <summary>--game &lt;Id&gt; startet direkt in einem Spiel; sonst die Sandbox</summary>
    private static string StartGameId(string[] args, GameRegistry registry)
    {
        int index = Array.IndexOf(args, "--game");
        if (index < 0 || index + 1 >= args.Length) return DefaultGame;

        string requested = args[index + 1];
        bool known = registry.Entries.Any(entry =>
            string.Equals(entry.Id, requested, StringComparison.OrdinalIgnoreCase));

        if (!known)
        {
            Console.Error.WriteLine($"Unbekanntes Spiel '{requested}'. Bekannt: {string.Join(", ", registry.Entries.Select(entry => entry.Id))}");
            return DefaultGame;
        }

        return registry.Entries.First(entry =>
            string.Equals(entry.Id, requested, StringComparison.OrdinalIgnoreCase)).Id;
    }

    private static bool HasFlag(string[] args, string flag)
        => Array.Exists(args, argument => argument == flag);
}
