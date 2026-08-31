using Raylib_cs;
using System.Numerics;
using Terraformer.Config;
using Terraformer.Effects;
using Terraformer.Rendering;
using Terraformer.UI;
using Terraformer.World;

namespace Terraformer;

public static class Program
{
    public static void Main(string[] args)
    {
        const int screenWidth = 1280;
        const int screenHeight = 720;

        // Mini-Testmodus: rendert ein paar Sekunden, legt einen Screenshot ab und beendet sich
        bool smokeTest = Array.Exists(args, argument => argument == "--smoke");

        Raylib.InitWindow(screenWidth, screenHeight, "Terraformer");
        Raylib.SetTargetFPS(60);
        if (!smokeTest) Raylib.DisableCursor();
        else Raylib.SetMousePosition(screenWidth / 2, screenHeight / 2); // sonst verdreht das erste Maus-Delta die Testkamera

        DebugSettings settings = DebugSettings.Load();

        VoxelWorld world = new VoxelWorld();

        Vector3 worldCenter = new(
            VoxelWorld.StartChunksX * Chunk.Size / 2f, 0,
            VoxelWorld.StartChunksZ * Chunk.Size / 2f);

        // Spieler mittig in der Startwelt spawnen (etwas über dem Terrain);
        // im Testlauf stattdessen mit Blick quer über die Welt
        PlayerController player = smokeTest
            ? new PlayerController(new Vector3(40, 58, 220), settings)
            : new PlayerController(worldCenter + new Vector3(0, 60, 0), settings);

        DayNightCycle dayNight = new DayNightCycle
        {
            Center = worldCenter,
            DayLengthSeconds = 240f,
            OrbitRadius = 300f,
            DrawSunAndMoon = true
        };

        // Testlauf zur Mittagszeit, damit auf dem Screenshot etwas zu erkennen ist
        if (smokeTest) dayNight.AdvanceTime(60f);

        TerrainShader terrainShader = new TerrainShader();
        ChunkMeshManager meshManager = new ChunkMeshManager(world);
        meshManager.BuildAllNow();

        ParticleSystem particles = new ParticleSystem();
        world.BlockBroken += particles.SpawnBlockBreak;
        world.BlockPlaced += particles.SpawnBlockPlace;

        DebugMenu debugMenu = new DebugMenu(settings);

        int smokeFrames = 0;
        bool debugOverlay = smokeTest; // im Testlauf direkt an, damit die Stats auf dem Screenshot stehen

        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();

            if (Raylib.IsKeyPressed(KeyboardKey.F3)) debugOverlay = !debugOverlay;

            debugMenu.Update();

            // Player bewegt sich + liefert Kamera
            Camera3D camera = player.Update(world, dt);

            // Day/Night Update (Speed: Z/U)
            dayNight.Update(dt);

            // Welt-Interaktion
            world.Update(camera, player.Bounds);

            // Geänderte Chunks meshen (Worker-Thread) bzw. fertige Meshes hochladen
            meshManager.Update();

            // Partikel laufen über den unbeleuchteten Default-Shader → Weltlicht beim Spawn einbacken
            Vector3 light = dayNight.AmbientColor + dayNight.SunlightColor * 0.8f;
            particles.LightScale = Math.Clamp((light.X + light.Y + light.Z) / 3f, 0.15f, 1.1f);
            particles.Update(world, dt);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(dayNight.SkyColor);

            Raylib.BeginMode3D(camera);

            terrainShader.SetFrame(dayNight.SunDirection, dayNight.SunlightColor, dayNight.AmbientColor);

            Frustum frustum = Frustum.FromCamera(
                camera, Raylib.GetScreenWidth() / (float)Raylib.GetScreenHeight());
            meshManager.Draw(terrainShader.Material, frustum);

            world.DrawHover();
            particles.Draw();
            dayNight.Draw3D(camera);

            if (debugOverlay)
            {
                GridRenderer.DrawFromOrigin(VoxelWorld.StartChunksX * Chunk.Size, 1.0f);
                meshManager.DrawChunkBounds();
            }

            Raylib.EndMode3D();

            // UI
            Raylib.DrawFPS(10, 10);
            Raylib.DrawText("WASD move | Shift sprint | Space jump | LMB remove | RMB place", 10, 40, 20, Color.Black);
            Raylib.DrawText("Z/U day speed | F3 debug | M tuning", 10, 65, 20, Color.Black);
            Raylib.DrawText(dayNight.SpeedLabel, 10, 90, 20, Color.Black);

            if (debugOverlay)
            {
                string stats =
                    $"Chunks {meshManager.VisibleChunks}/{meshManager.MeshedChunks} | " +
                    $"Verts {meshManager.TotalVertices / 1000}k | " +
                    $"Queue {meshManager.PendingChunks} | " +
                    $"Particles {particles.ActiveParticles}";
                Raylib.DrawText(stats, 10, 115, 20, Color.DarkBlue);
            }

            // Crosshair
            int cx = Raylib.GetScreenWidth() / 2;
            int cy = Raylib.GetScreenHeight() / 2;
            Raylib.DrawCircle(cx, cy, 4, Color.Black);

            debugMenu.Draw(Raylib.GetScreenWidth());

            Raylib.EndDrawing();

            if (smokeTest && ++smokeFrames >= 150)
            {
                Raylib.TakeScreenshot("smoke.png");
                break;
            }
        }

        settings.Save();
        meshManager.Dispose();
        terrainShader.Unload();
        Raylib.CloseWindow();
    }
}
