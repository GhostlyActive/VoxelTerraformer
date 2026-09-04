using VoxelEngine.Config;
using Xunit;

namespace VoxelEngine.Tests;

public class SettingsStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "voxel-settings-test-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void SectionsRoundTripThroughTheFile()
    {
        string file = TempFile();
        try
        {
            var store = new SettingsStore(file);
            store.Save("Engine", new EngineSettings { FieldOfView = 75f });
            store.Save("FreeWalk/Terrain", new TerrainSettings { WalkSpeed = 12f });

            var reloaded = new SettingsStore(file);
            Assert.Equal(75f, reloaded.Load<EngineSettings>("Engine").FieldOfView);
            Assert.Equal(12f, reloaded.Load<TerrainSettings>("FreeWalk/Terrain").WalkSpeed);
            Assert.Equal(new TerrainSettings().WalkSpeed, reloaded.Load<TerrainSettings>("RocketStorm/Terrain").WalkSpeed);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AFlatFileFromBeforeTheSplitSeedsEverySection()
    {
        string file = TempFile();
        try
        {
            File.WriteAllText(file, """{ "MouseSensitivity": 0.3, "WalkSpeed": 9.5, "FogEnd": 700 }""");

            var store = new SettingsStore(file);
            Assert.Equal(0.3f, store.Load<EngineSettings>("Engine").MouseSensitivity);
            Assert.Equal(9.5f, store.Load<TerrainSettings>("FreeWalk/Terrain").WalkSpeed);
            Assert.Equal(700f, store.Load<TerrainSettings>("RocketStorm/Terrain").FogEnd);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ABrokenFileFallsBackToTheDefaults()
    {
        string file = TempFile();
        try
        {
            File.WriteAllText(file, "{ not json");

            var store = new SettingsStore(file);
            Assert.Equal(new EngineSettings().FieldOfView, store.Load<EngineSettings>("Engine").FieldOfView);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
